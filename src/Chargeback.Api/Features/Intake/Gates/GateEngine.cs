using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Api.Features.Intake.Normalisation;
using Chargeback.SharedKernel.ValueObjects;
using Microsoft.Extensions.Options;

namespace Chargeback.Api.Features.Intake.Gates;

/// <summary>
/// Result of one gate evaluator. <c>Passed = null</c> means no pass/fail decision was made; the
/// <see cref="Status"/> says why. An undecided or errored gate is never a successful validation (ADR-0117).
/// </summary>
public sealed record GateOutcome(bool? Passed, string? FlagReason, GateExecutionStatus Status)
{
    public const string PendingDefinitionCode = "PENDING_DEFINITION";
    public const string GateErrorCode = "GATE_ERROR";

    public static GateOutcome Pass() => new(true, null, GateExecutionStatus.Passed);

    public static GateOutcome Fail(string reason) => new(false, reason, GateExecutionStatus.Failed);

    /// <summary>The gate's criteria are not approved (or its evaluator is not activated).</summary>
    public static GateOutcome PendingDefinition(string detail) =>
        new(null, $"{PendingDefinitionCode}: {detail}", GateExecutionStatus.PendingDefinition);

    /// <summary>The gate could not run (dependency down, unexpected error). Distinct from pending definition.</summary>
    public static GateOutcome TechnicalError(string detail) =>
        new(null, $"{GateErrorCode}: {detail}", GateExecutionStatus.TechnicalError);
}

public sealed record GateContext(NormalisedIntakePackage Package, DateTimeOffset EvaluatedAt);

/// <summary>
/// Business criteria for one gate. Implementations are added only once the gate's thresholds,
/// applicability and failure policy are signed off, and run only when activated in configuration
/// (<see cref="GateOptions.ActiveGates"/>). Evaluators must signal technical problems with
/// <see cref="GateOutcome.TechnicalError"/> (or by throwing), never with Pass.
/// </summary>
public interface IGateEvaluator
{
    int GateNumber { get; }

    Task<GateOutcome> EvaluateAsync(GateContext context, CancellationToken cancellationToken);
}

/// <summary>Configuration section <c>Intake:Gates</c>. Nothing is active by default.</summary>
public sealed class GateOptions
{
    public const string SectionName = "Intake:Gates";

    /// <summary>Gate numbers whose registered evaluator may run. Activate a gate only after its criteria are approved.</summary>
    public int[] ActiveGates { get; set; } = [];
}

/// <summary>Per-gate result with its execution status (the status is also derivable from the stored reason prefix).</summary>
public sealed record GateEvaluationResult(GateResult Result, GateExecutionStatus Status);

public sealed record GateEvaluation(string RegistryVersion, IReadOnlyList<GateEvaluationResult> Gates)
{
    public IReadOnlyList<GateResult> Results => Gates.Select(g => g.Result).ToArray();

    /// <summary>Diagram rule: all ten pass → NEW; anything else (fail, pending, error) → FLAGGED for an analyst.</summary>
    public bool AllPassed => Gates.Count > 0 && Gates.All(g => g.Status == GateExecutionStatus.Passed && g.Result.Passed == true);

    public string DisputeStatus => AllPassed ? DisputeStatuses.New : DisputeStatuses.Flagged;

    public IReadOnlyList<int> GatesWith(GateExecutionStatus status) =>
        Gates.Where(g => g.Status == status).Select(g => g.Result.GateNumber).ToArray();
}

public interface IGateEngine
{
    Task<GateEvaluation> EvaluateAsync(NormalisedIntakePackage package, CancellationToken cancellationToken);
}

/// <summary>
/// Runs every registry gate in the approved order and records one result per gate (ADR-0117 approved):
/// later gates still run after a failure; gates without approved/activated criteria are PENDING_DEFINITION;
/// evaluator exceptions are GATE_ERROR (technical) and never lose the dispute.
/// Runs outside any database transaction, so future evaluators may call external systems.
/// </summary>
internal sealed partial class GateEngine : IGateEngine
{
    private readonly IGateRegistry _registry;
    private readonly IReadOnlyDictionary<int, IGateEvaluator> _evaluators;
    private readonly IReadOnlySet<int> _activeGates;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GateEngine> _logger;

    /// <summary>Production constructor: evaluators run only when listed in <see cref="GateOptions.ActiveGates"/>.</summary>
    public GateEngine(
        IGateRegistry registry,
        IEnumerable<IGateEvaluator> evaluators,
        IOptions<GateOptions> options,
        TimeProvider timeProvider,
        ILogger<GateEngine> logger)
        : this(registry, evaluators, options.Value.ActiveGates, timeProvider, logger)
    {
    }

    /// <summary>Test constructor: every supplied evaluator is active.</summary>
    internal GateEngine(IGateRegistry registry, IEnumerable<IGateEvaluator> evaluators, TimeProvider timeProvider, ILogger<GateEngine> logger)
        : this(registry, evaluators.ToArray(), (IEnumerable<int>?)null, timeProvider, logger)
    {
    }

    private GateEngine(
        IGateRegistry registry,
        IEnumerable<IGateEvaluator> evaluators,
        IEnumerable<int>? activeGates,
        TimeProvider timeProvider,
        ILogger<GateEngine> logger)
    {
        _registry = registry;
        _timeProvider = timeProvider;
        _logger = logger;

        var byNumber = new Dictionary<int, IGateEvaluator>();
        foreach (var evaluator in evaluators)
        {
            if (registry.Gates.All(g => g.Number != evaluator.GateNumber))
            {
                throw new InvalidOperationException($"Evaluator {evaluator.GetType().Name} targets unknown gate {evaluator.GateNumber}.");
            }

            if (!byNumber.TryAdd(evaluator.GateNumber, evaluator))
            {
                throw new InvalidOperationException($"More than one evaluator is registered for gate {evaluator.GateNumber}.");
            }
        }

        _evaluators = byNumber;
        _activeGates = (activeGates ?? byNumber.Keys).ToHashSet();
    }

    public async Task<GateEvaluation> EvaluateAsync(NormalisedIntakePackage package, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);

        var results = new List<GateEvaluationResult>(_registry.Gates.Count);
        foreach (var gate in _registry.Gates)
        {
            var checkedAt = _timeProvider.GetUtcNow();
            var outcome = await EvaluateGateAsync(gate, new GateContext(package, checkedAt), cancellationToken);
            results.Add(new GateEvaluationResult(
                new GateResult(gate.Number, gate.Name, outcome.Passed, outcome.FlagReason, checkedAt),
                outcome.Status));
        }

        return new GateEvaluation(_registry.Version, results);
    }

    private async Task<GateOutcome> EvaluateGateAsync(GateDefinition gate, GateContext context, CancellationToken cancellationToken)
    {
        if (!_evaluators.TryGetValue(gate.Number, out var evaluator))
        {
            return GateOutcome.PendingDefinition($"criteria for '{gate.Name}' are awaiting business sign-off.");
        }

        if (!_activeGates.Contains(gate.Number))
        {
            return GateOutcome.PendingDefinition($"evaluator for '{gate.Name}' is not activated in configuration.");
        }

        try
        {
            var outcome = await evaluator.EvaluateAsync(context, cancellationToken);
            return IsConsistent(outcome)
                ? outcome
                : GateOutcome.TechnicalError($"'{gate.Name}' returned an inconsistent outcome.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogGateError(_logger, gate.Number, gate.Name, ex);
            return GateOutcome.TechnicalError($"'{gate.Name}' could not be evaluated.");
        }
    }

    /// <summary>Guards against evaluators claiming success while withholding a decision, or vice versa.</summary>
    private static bool IsConsistent(GateOutcome outcome) => outcome.Status switch
    {
        GateExecutionStatus.Passed => outcome.Passed == true,
        GateExecutionStatus.Failed => outcome.Passed == false,
        GateExecutionStatus.PendingDefinition or GateExecutionStatus.TechnicalError => outcome.Passed is null,
        _ => false,
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Gate {GateNumber} ({GateName}) evaluation failed; recorded as technical error")]
    private static partial void LogGateError(ILogger logger, int gateNumber, string gateName, Exception exception);
}
