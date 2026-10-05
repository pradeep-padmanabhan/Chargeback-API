using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Api.Features.Intake.Gates;
using Chargeback.Api.Features.Intake.Normalisation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Chargeback.UnitTests.Intake;

/// <summary>ADR-0117 (approved): distinct pending-definition vs technical-error handling and configurable activation.</summary>
public sealed class GateStatusTests
{
    private static readonly NormalisedIntakePackage Package = new(Guid.NewGuid(), IntakeChannel.Portal, null, null, null, null, null, null, null);

    private static GateEngine ConfiguredEngine(int[] activeGates, params IGateEvaluator[] evaluators) =>
        new(new ApprovedGateRegistry(), evaluators, Options.Create(new GateOptions { ActiveGates = activeGates }), TimeProvider.System, NullLogger<GateEngine>.Instance);

    [Fact]
    public async Task Registered_but_inactive_evaluator_is_pending_definition()
    {
        var engine = ConfiguredEngine([], new PassGate(1));

        var gate1 = (await engine.EvaluateAsync(Package, CancellationToken.None)).Gates[0];

        gate1.Status.Should().Be(GateExecutionStatus.PendingDefinition);
        gate1.Result.Passed.Should().BeNull();
        gate1.Result.FlagReason.Should().Contain("not activated");
    }

    [Fact]
    public async Task Active_evaluator_runs()
    {
        var gate1 = (await ConfiguredEngine([1], new PassGate(1)).EvaluateAsync(Package, CancellationToken.None)).Gates[0];

        gate1.Status.Should().Be(GateExecutionStatus.Passed);
    }

    [Fact]
    public async Task Pending_and_technical_error_are_distinct_and_neither_passes()
    {
        var evaluation = await ConfiguredEngine([1, 2], new ThrowGate(1), new ExplicitErrorGate(2)).EvaluateAsync(Package, CancellationToken.None);

        evaluation.GatesWith(GateExecutionStatus.TechnicalError).Should().Equal(1, 2);
        evaluation.GatesWith(GateExecutionStatus.PendingDefinition).Should().Equal(3, 4, 5, 6, 7, 8, 9, 10);
        evaluation.Results[0].FlagReason.Should().StartWith(GateOutcome.GateErrorCode);
        evaluation.Results[2].FlagReason.Should().StartWith(GateOutcome.PendingDefinitionCode);
        evaluation.AllPassed.Should().BeFalse();
        evaluation.DisputeStatus.Should().Be(DisputeStatuses.Flagged);
    }

    [Fact]
    public async Task Evaluator_claiming_pass_without_a_decision_is_a_technical_error()
    {
        var evaluation = await ConfiguredEngine([1], new InconsistentGate(1)).EvaluateAsync(Package, CancellationToken.None);

        evaluation.Gates[0].Status.Should().Be(GateExecutionStatus.TechnicalError);
    }

    [Theory]
    [InlineData(true, null, GateExecutionStatus.Passed)]
    [InlineData(false, "x", GateExecutionStatus.Failed)]
    [InlineData(null, "PENDING_DEFINITION: x", GateExecutionStatus.PendingDefinition)]
    [InlineData(null, "GATE_ERROR: x", GateExecutionStatus.TechnicalError)]
    [InlineData(null, "something else", GateExecutionStatus.TechnicalError)]
    [InlineData(null, null, GateExecutionStatus.TechnicalError)]
    public void Stored_status_is_recovered_and_never_optimistic(bool? passed, string? reason, GateExecutionStatus expected)
    {
        GateExecutionStatuses.FromStored(passed, reason).Should().Be(expected);
    }

    private sealed class PassGate(int n) : IGateEvaluator
    {
        public int GateNumber => n;

        public Task<GateOutcome> EvaluateAsync(GateContext context, CancellationToken cancellationToken) => Task.FromResult(GateOutcome.Pass());
    }

    private sealed class ThrowGate(int n) : IGateEvaluator
    {
        public int GateNumber => n;

        public Task<GateOutcome> EvaluateAsync(GateContext context, CancellationToken cancellationToken) => throw new TimeoutException("synthetic");
    }

    private sealed class ExplicitErrorGate(int n) : IGateEvaluator
    {
        public int GateNumber => n;

        public Task<GateOutcome> EvaluateAsync(GateContext context, CancellationToken cancellationToken) =>
            Task.FromResult(GateOutcome.TechnicalError("synthetic dependency unavailable"));
    }

    private sealed class InconsistentGate(int n) : IGateEvaluator
    {
        public int GateNumber => n;

        public Task<GateOutcome> EvaluateAsync(GateContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new GateOutcome(null, "claims pass", GateExecutionStatus.Passed));
    }
}
