using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Api.Features.Intake.Gates;
using Chargeback.Api.Features.Intake.Normalisation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Chargeback.UnitTests.Intake;

public sealed class GateRegistryTests
{
    [Fact]
    public void Approved_registry_is_the_er_diagram_list_in_order()
    {
        var registry = new ApprovedGateRegistry();

        registry.Gates.Select(g => (g.Number, g.Name)).Should().Equal(
            (1, "Required Fields"),
            (2, "Transaction Lookup"),
            (3, "Card/Account Check"),
            (4, "Amount/Currency Check"),
            (5, "Time Window Check"),
            (6, "Duplicate Check"),
            (7, "Merchant/Category Check"),
            (8, "Reason Code Derivation"),
            (9, "Document Requirement"),
            (10, "Compliance Check"));
        registry.Version.Should().Be(ApprovedGateRegistry.RegistryVersion);
    }

    [Fact]
    public void Superseded_process_flow_labels_are_not_used()
    {
        var names = new ApprovedGateRegistry().Gates.Select(g => g.Name).ToArray();

        names.Should().NotContain(["Completeness & Consistency", "Rogatory/Regulatory Orders", "Regulatory Position (UK)"]);
    }

    [Fact]
    public void Registry_must_have_exactly_ten_gates()
    {
        var act = () => ApprovedGateRegistry.Validate([new(1, "Only")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*exactly 10*");
    }

    [Fact]
    public void Registry_rejects_gaps_or_reordering()
    {
        var gates = Enumerable.Range(1, 10).Select(n => new GateDefinition(n == 3 ? 4 : n == 4 ? 3 : n, $"G{n}")).ToArray();

        var act = () => ApprovedGateRegistry.Validate(gates);

        act.Should().Throw<InvalidOperationException>().WithMessage("*1..10*");
    }
}

public sealed class GateEngineTests
{
    private static readonly NormalisedIntakePackage Package =
        new(Guid.NewGuid(), IntakeChannel.Portal, null, null, null, null, null, null, null);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));

    private GateEngine Engine(params IGateEvaluator[] evaluators) =>
        new(new ApprovedGateRegistry(), evaluators, _time, NullLogger<GateEngine>.Instance);

    [Fact]
    public async Task Gates_without_approved_criteria_are_undecided_and_flag_the_dispute()
    {
        var evaluation = await Engine().EvaluateAsync(Package, CancellationToken.None);

        evaluation.Results.Should().HaveCount(10);
        evaluation.Results.Should().OnlyContain(r => r.Passed == null && r.FlagReason!.StartsWith(GateOutcome.PendingDefinitionCode));
        evaluation.AllPassed.Should().BeFalse();
        evaluation.DisputeStatus.Should().Be(DisputeStatuses.Flagged);
        evaluation.RegistryVersion.Should().Be(ApprovedGateRegistry.RegistryVersion);
    }

    [Fact]
    public async Task All_gates_passing_makes_the_dispute_new()
    {
        var evaluators = Enumerable.Range(1, 10).Select(n => (IGateEvaluator)new FixedGate(n, GateOutcome.Pass())).ToArray();

        var evaluation = await Engine(evaluators).EvaluateAsync(Package, CancellationToken.None);

        evaluation.DisputeStatus.Should().Be(DisputeStatuses.New);
        evaluation.Results.Select(r => r.GateNumber).Should().Equal(Enumerable.Range(1, 10));
        evaluation.Results.Should().OnlyContain(r => r.CheckedAt == _time.GetUtcNow());
    }

    [Fact]
    public async Task One_failure_flags_but_every_gate_still_runs()
    {
        var evaluators = Enumerable.Range(1, 10)
            .Select(n => (IGateEvaluator)new FixedGate(n, n == 2 ? GateOutcome.Fail("ARN not found") : GateOutcome.Pass()))
            .ToArray();

        var evaluation = await Engine(evaluators).EvaluateAsync(Package, CancellationToken.None);

        evaluation.DisputeStatus.Should().Be(DisputeStatuses.Flagged);
        evaluation.Results.Should().HaveCount(10);
        evaluation.Results.Single(r => r.GateNumber == 2).Should().Match<Chargeback.SharedKernel.ValueObjects.GateResult>(r => r.Passed == false && r.FlagReason == "ARN not found");
        evaluators.Cast<FixedGate>().Should().OnlyContain(g => g.Calls == 1);
    }

    [Fact]
    public async Task Evaluator_exception_is_recorded_as_undecided_not_lost()
    {
        var evaluation = await Engine(new ThrowingGate(5)).EvaluateAsync(Package, CancellationToken.None);

        var gate5 = evaluation.Results.Single(r => r.GateNumber == 5);
        gate5.Passed.Should().BeNull();
        gate5.FlagReason.Should().StartWith(GateOutcome.GateErrorCode);
        evaluation.DisputeStatus.Should().Be(DisputeStatuses.Flagged);
    }

    [Fact]
    public async Task Request_cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Engine(new CancellingGate(1)).EvaluateAsync(Package, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Duplicate_or_unknown_evaluators_are_rejected()
    {
        var duplicate = () => Engine(new FixedGate(1, GateOutcome.Pass()), new FixedGate(1, GateOutcome.Pass()));
        var unknown = () => Engine(new FixedGate(11, GateOutcome.Pass()));

        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*more than one*");
        unknown.Should().Throw<InvalidOperationException>().WithMessage("*unknown gate 11*");
    }

    private sealed class FixedGate(int number, GateOutcome outcome) : IGateEvaluator
    {
        public int Calls { get; private set; }

        public int GateNumber => number;

        public Task<GateOutcome> EvaluateAsync(GateContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(outcome);
        }
    }

    private sealed class ThrowingGate(int number) : IGateEvaluator
    {
        public int GateNumber => number;

        public Task<GateOutcome> EvaluateAsync(GateContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("dependency down");
    }

    private sealed class CancellingGate(int number) : IGateEvaluator
    {
        public int GateNumber => number;

        public Task<GateOutcome> EvaluateAsync(GateContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GateOutcome.Pass());
        }
    }
}
