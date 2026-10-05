using Chargeback.Api.Features.Triage.IssuerTriage;
using Chargeback.Api.Features.Triage.SchemeRules;
using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.UnitTests.Triage;

/// <summary>Engine mechanics with a SYNTHETIC bank configuration (see <see cref="Synthetic"/>); not bank policy.</summary>
public sealed class IssuerTriageEngineTests
{
    private static Task<IssuerTriageEvaluation> Evaluate(
        BankTriageConfiguration config, decimal amount = 100m, string currency = "GBP", SchemeRuleEvaluation? scheme = null) =>
        Evaluate(BankTriageConfigurationResult.Approved(config), amount, currency, scheme);

    private static Task<IssuerTriageEvaluation> Evaluate(
        BankTriageConfigurationResult config, decimal amount = 100m, string currency = "GBP", SchemeRuleEvaluation? scheme = null) =>
        new IssuerTriageEngine(new FixedConfigProvider(config))
            .EvaluateAsync(Guid.NewGuid(), Synthetic.Facts(currency, amount), scheme ?? Synthetic.DeterminedScheme(), DateTimeOffset.UtcNow, CancellationToken.None);

    private static RoutingRule Route(TriageOutcome outcome, string condition = """{"fact":"dispute.intakeChannel","op":"eq","value":"PORTAL"}""") =>
        new("SYN-ROUTE-" + outcome, condition, outcome);

    [Theory]
    [InlineData(TriageOutcome.ProceedToFiling)]
    [InlineData(TriageOutcome.AutoRefund)]
    [InlineData(TriageOutcome.SendToCompliance)]
    [InlineData(TriageOutcome.Defer)]
    public async Task Routing_policy_decides_configured_outcomes(TriageOutcome outcome)
    {
        var result = await Evaluate(Synthetic.BankConfig(routing: [Route(outcome)]));

        result.IsComplete.Should().BeTrue();
        result.Outcome.Should().Be(outcome);
        result.RoutingRuleId.Should().Be("SYN-ROUTE-" + outcome);
        result.HardEligibilityPass.Should().BeTrue();
        result.HumanReviewTriggered.Should().BeFalse();
        result.ConfigurationVersion.Should().Be("SYN-CONFIG-1");
    }

    [Fact]
    public async Task Failed_hard_eligibility_decides_its_configured_outcome_invalid()
    {
        var config = Synthetic.BankConfig(eligibility: [new("SYN-ELIG-GBP", """{"fact":"dispute.currencyCode","op":"eq","value":"GBP"}""", TriageOutcome.Invalid)]);

        var result = await Evaluate(config, currency: "EUR");

        result.Outcome.Should().Be(TriageOutcome.Invalid);
        result.HardEligibilityPass.Should().BeFalse();
        result.Components.Where(c => c.Component != TriageComponent.HardEligibility).Should().OnlyContain(c => c.Status == ComponentStatus.NotEvaluated);
    }

    [Fact]
    public async Task Risk_threshold_routes_to_human_with_score_and_flags()
    {
        var result = await Evaluate(Synthetic.BankConfig(), amount: 6000m);

        result.Outcome.Should().Be(TriageOutcome.RouteToHuman);
        result.RiskScore.Should().Be(60m);
        result.RiskFlags.Should().Equal("SYN-RISK-HIGH-AMOUNT");
        result.HumanReviewTriggered.Should().BeTrue();
        result.HumanReviewReason.Should().Contain("RISK_THRESHOLD");
    }

    [Fact]
    public async Task Human_review_trigger_routes_to_human_even_when_routing_would_file()
    {
        var config = Synthetic.BankConfig(triggers: [new("SYN-TRIGGER-SHOP", """{"fact":"dispute.merchantName","op":"eq","value":"SYN merchant"}""", "synthetic review reason")]);

        var result = await Evaluate(config);

        result.Outcome.Should().Be(TriageOutcome.RouteToHuman);
        result.HumanReviewReason.Should().Contain("SYN-TRIGGER-SHOP");
        result.RoutingRuleId.Should().BeNull();
    }

    [Fact]
    public async Task Hard_eligibility_failure_takes_precedence_over_risk()
    {
        var config = Synthetic.BankConfig(eligibility: [new("SYN-ELIG", """{"fact":"dispute.transactionAmount","op":"lt","value":1000}""", TriageOutcome.SendToCompliance)]);

        (await Evaluate(config, amount: 6000m)).Outcome.Should().Be(TriageOutcome.SendToCompliance);
    }

    [Fact]
    public async Task Routing_uses_the_first_matching_rule_in_order()
    {
        var config = Synthetic.BankConfig(routing: [Route(TriageOutcome.Defer, """{"fact":"dispute.currencyCode","op":"eq","value":"EUR"}"""), Route(TriageOutcome.AutoRefund), Route(TriageOutcome.ProceedToFiling)]);

        (await Evaluate(config)).Outcome.Should().Be(TriageOutcome.AutoRefund);
    }

    [Fact]
    public async Task All_six_outcomes_are_reachable_only_through_configuration()
    {
        var reached = new HashSet<TriageOutcome>();
        foreach (var outcome in new[] { TriageOutcome.ProceedToFiling, TriageOutcome.AutoRefund, TriageOutcome.SendToCompliance, TriageOutcome.Defer })
        {
            reached.Add((await Evaluate(Synthetic.BankConfig(routing: [Route(outcome)]))).Outcome!.Value);
        }

        reached.Add((await Evaluate(Synthetic.BankConfig(), amount: 6000m)).Outcome!.Value);
        reached.Add((await Evaluate(Synthetic.BankConfig(eligibility: [new("SYN-E", """{"fact":"dispute.currencyCode","op":"eq","value":"JPY"}""", TriageOutcome.Invalid)]))).Outcome!.Value);

        reached.Should().BeEquivalentTo(Enum.GetValues<TriageOutcome>());
    }

    [Fact]
    public async Task Missing_bank_configuration_is_incomplete_with_no_outcome()
    {
        var result = await Evaluate(BankTriageConfigurationResult.NotConfigured("BANK_CONFIGURATION_PENDING_APPROVAL"));

        result.IsComplete.Should().BeFalse();
        result.IsUnavailable.Should().BeFalse();
        result.Outcome.Should().BeNull();
        result.HumanReviewTriggered.Should().BeTrue();
        result.HumanReviewReason.Should().StartWith(IssuerTriageEngine.IncompletePrefix);
    }

    [Fact]
    public async Task Production_default_provider_has_no_approved_configuration()
    {
        var result = await new PendingApprovalBankTriageConfigurationProvider().GetApprovedAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, CancellationToken.None);

        result.Status.Should().Be(BankTriageConfigurationStatus.NotConfigured);
        result.Detail.Should().Contain("ADR-0119");
    }

    [Fact]
    public async Task Unreadable_configuration_is_flagged_unavailable()
    {
        var result = await Evaluate(BankTriageConfigurationResult.Unavailable("store down"));

        result.IsUnavailable.Should().BeTrue();
        result.Outcome.Should().BeNull();
    }

    [Fact]
    public async Task Scheme_layer_not_determined_means_issuer_layer_is_not_evaluated()
    {
        var scheme = SchemeRuleEvaluation.NotDetermined(SchemeEvaluationStatus.NoMatch, "synthetic no match");

        var result = await Evaluate(Synthetic.BankConfig(), scheme: scheme);

        result.IsComplete.Should().BeFalse();
        result.DecidingLayer.Should().Be(IssuerTriageEngine.SchemeLayer);
        result.Components.Should().OnlyContain(c => c.Status == ComponentStatus.NotEvaluated);
    }

    [Theory]
    [InlineData("eligibility")]
    [InlineData("risk")]
    [InlineData("routing")]
    public async Task Unknown_facts_in_any_component_make_the_evaluation_incomplete(string component)
    {
        const string needsDate = """{"fact":"dispute.transactionDate","op":"lt","value":"2026-01-01"}""";
        var config = component switch
        {
            "eligibility" => Synthetic.BankConfig(eligibility: [new("SYN-E", needsDate, TriageOutcome.Invalid)]),
            "risk" => Synthetic.BankConfig(risk: new([new("SYN-R", needsDate, 1m)], 50m)),
            _ => Synthetic.BankConfig(routing: [Route(TriageOutcome.Defer, needsDate), Route(TriageOutcome.ProceedToFiling)]),
        };

        var result = await Evaluate(config);

        result.IsComplete.Should().BeFalse();
        result.Outcome.Should().BeNull();
        result.Components.Should().Contain(c => c.Status == ComponentStatus.Incomplete);
    }

    [Fact]
    public async Task No_matching_routing_rule_is_incomplete_not_a_default_outcome()
    {
        var result = await Evaluate(Synthetic.BankConfig(routing: [Route(TriageOutcome.ProceedToFiling, """{"fact":"dispute.currencyCode","op":"eq","value":"EUR"}""")]));

        result.IsComplete.Should().BeFalse();
        result.Detail.Should().Contain("no routing policy rule matched");
    }

    [Theory]
    [InlineData("condition")]
    [InlineData("duplicate-id")]
    [InlineData("weights")]
    public async Task Invalid_configuration_is_incomplete(string problem)
    {
        var config = problem switch
        {
            "condition" => Synthetic.BankConfig(routing: [new("SYN-X", "{}", TriageOutcome.ProceedToFiling)]),
            "duplicate-id" => Synthetic.BankConfig(routing: [Route(TriageOutcome.Defer), Route(TriageOutcome.Defer)]),
            _ => Synthetic.BankConfig(risk: new([new("SYN-R", """{"fact":"dispute.currencyCode","op":"exists"}""", 10000m)], 1m)),
        };

        var result = await Evaluate(config);

        result.IsComplete.Should().BeFalse();
        result.Detail.Should().Contain("invalid bank configuration");
    }

    [Fact]
    public async Task Issuer_rules_may_use_the_determined_reason_code_fact()
    {
        var config = Synthetic.BankConfig(routing: [Route(TriageOutcome.SendToCompliance, """{"fact":"scheme.reasonCode","op":"eq","value":"SYN-COMPLIANCE"}"""), Route(TriageOutcome.ProceedToFiling)]);

        (await Evaluate(config, scheme: Synthetic.DeterminedScheme("SYN-COMPLIANCE"))).Outcome.Should().Be(TriageOutcome.SendToCompliance);
        (await Evaluate(config, scheme: Synthetic.DeterminedScheme("SYN-OTHER"))).Outcome.Should().Be(TriageOutcome.ProceedToFiling);
    }
}
