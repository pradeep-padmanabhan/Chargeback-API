using Chargeback.Api.Features.Triage.Conditions;
using Chargeback.Api.Features.Triage.SchemeRules;
using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.Api.Features.Triage.IssuerTriage;

public enum TriageComponent
{
    HardEligibility,
    RiskScoring,
    HumanReviewTrigger,
    RoutingPolicy,
}

public enum ComponentStatus
{
    Evaluated,
    NotEvaluated,
    Incomplete,
}

public sealed record ComponentAudit(TriageComponent Component, ComponentStatus Status, string Detail);

/// <summary>
/// Issuer triage result. <see cref="Outcome"/> is null when the evaluation is incomplete — never a
/// manufactured default. An outcome is a recorded recommendation only: it executes no refund or filing.
/// </summary>
public sealed record IssuerTriageEvaluation(
    bool IsComplete,
    bool IsUnavailable,
    TriageOutcome? Outcome,
    string DecidingLayer,
    string Detail,
    bool? HardEligibilityPass,
    string? RoutingRuleId,
    decimal? RiskScore,
    IReadOnlyList<string> RiskFlags,
    bool HumanReviewTriggered,
    string? HumanReviewReason,
    string? ConfigurationVersion,
    IReadOnlyList<ComponentAudit> Components);

public interface IIssuerTriageEngine
{
    Task<IssuerTriageEvaluation> EvaluateAsync(Guid bankId, CaseFacts facts, SchemeRuleEvaluation scheme, DateTimeOffset asOf, CancellationToken cancellationToken);
}

/// <summary>
/// Issuer triage layer (common guide §6 Act 3.2) — Hard Eligibility, Risk Scoring, Human Review Triggers,
/// Routing Policy — driven only by the bank's approved configuration. Evaluation order (proposed, ADR-0119):
/// <list type="number">
/// <item>scheme layer must be Determined, otherwise incomplete;</item>
/// <item>hard eligibility: a rule that does not hold decides its configured outcome;</item>
/// <item>risk scoring and human-review triggers: either firing decides RouteToHuman;</item>
/// <item>routing policy: the first matching rule decides the outcome.</item>
/// </list>
/// Any unknown fact, missing or invalid configuration, or unmatched routing yields an explicit incomplete
/// result that is routed for manual attention.
/// </summary>
public sealed class IssuerTriageEngine(IBankTriageConfigurationProvider configurationProvider) : IIssuerTriageEngine
{
    public const string SchemeLayer = "SCHEME_RULES";
    public const string IssuerLayer = "ISSUER_TRIAGE";
    public const string IncompletePrefix = "TRIAGE_INCOMPLETE";
    public const decimal MaxRiskScore = 9999.9999m;
    public const int MaxRuleIdLength = 80;

    public async Task<IssuerTriageEvaluation> EvaluateAsync(
        Guid bankId, CaseFacts facts, SchemeRuleEvaluation scheme, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(scheme);

        if (!scheme.IsDetermined)
        {
            return Incomplete(SchemeLayer, $"scheme rules {scheme.Status}: {scheme.Detail}", NotEvaluatedAll("scheme layer not determined"));
        }

        BankTriageConfigurationResult configuration;
        try
        {
            configuration = await configurationProvider.GetApprovedAsync(bankId, asOf, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            configuration = BankTriageConfigurationResult.Unavailable("bank configuration could not be read");
        }

        switch (configuration.Status)
        {
            case BankTriageConfigurationStatus.Unavailable:
                return Incomplete(IssuerLayer, configuration.Detail, NotEvaluatedAll("configuration unavailable")) with { IsUnavailable = true };
            case BankTriageConfigurationStatus.NotConfigured or not BankTriageConfigurationStatus.Approved:
                return Incomplete(IssuerLayer, configuration.Detail, NotEvaluatedAll("no approved bank configuration"));
        }

        var config = configuration.Configuration!;
        var parsed = ParsedConfiguration.TryParse(config, out var problem);
        if (parsed is null)
        {
            return Incomplete(IssuerLayer, $"invalid bank configuration {config.Version}: {problem}", NotEvaluatedAll("invalid configuration"))
                with
            { ConfigurationVersion = config.Version };
        }

        facts = facts.With(FactCatalog.SchemeReasonCode, FactValue.Of(scheme.ReasonCode!.Code));
        return Decide(parsed, facts, config.Version);
    }

    private static IssuerTriageEvaluation Decide(ParsedConfiguration config, CaseFacts facts, string version)
    {
        var audits = new List<ComponentAudit>();

        // 1. Hard eligibility.
        var eligibility = config.HardEligibility.Select(r => (Rule: r, Result: r.Condition.Evaluate(facts))).ToArray();
        var failed = eligibility.FirstOrDefault(e => e.Result == TriState.False);
        if (failed.Rule is not null)
        {
            audits.Add(new(TriageComponent.HardEligibility, ComponentStatus.Evaluated, $"rule '{failed.Rule.Id}' not met"));
            audits.AddRange(NotEvaluated([TriageComponent.RiskScoring, TriageComponent.HumanReviewTrigger, TriageComponent.RoutingPolicy], "hard eligibility decided"));
            return Complete(failed.Rule.OutcomeWhenNotMet, "hard eligibility", version, audits) with { HardEligibilityPass = false };
        }

        if (eligibility.Any(e => e.Result == TriState.Unknown))
        {
            audits.Add(new(TriageComponent.HardEligibility, ComponentStatus.Incomplete, "missing facts for: " + Ids(eligibility.Where(e => e.Result == TriState.Unknown).Select(e => e.Rule.Id))));
            return Incomplete(IssuerLayer, "hard eligibility could not be decided", audits) with { ConfigurationVersion = version };
        }

        audits.Add(new(TriageComponent.HardEligibility, ComponentStatus.Evaluated, "all rules met"));

        // 2. Risk scoring.
        var factors = config.RiskFactors.Select(f => (Factor: f, Result: f.Condition.Evaluate(facts))).ToArray();
        if (factors.Any(f => f.Result == TriState.Unknown))
        {
            audits.Add(new(TriageComponent.RiskScoring, ComponentStatus.Incomplete, "missing facts for: " + Ids(factors.Where(f => f.Result == TriState.Unknown).Select(f => f.Factor.Id))));
            return Incomplete(IssuerLayer, "risk score could not be calculated", audits) with { ConfigurationVersion = version, HardEligibilityPass = true };
        }

        var flags = factors.Where(f => f.Result == TriState.True).Select(f => f.Factor.Id).ToArray();
        var score = factors.Where(f => f.Result == TriState.True).Sum(f => f.Factor.Weight);
        var riskTriggered = score >= config.RiskThreshold;
        audits.Add(new(TriageComponent.RiskScoring, ComponentStatus.Evaluated, $"score {score} vs threshold {config.RiskThreshold}"));

        // 3. Human review triggers.
        var triggers = config.HumanReview.Select(t => (Trigger: t, Result: t.Condition.Evaluate(facts))).ToArray();
        if (triggers.Any(t => t.Result == TriState.Unknown))
        {
            audits.Add(new(TriageComponent.HumanReviewTrigger, ComponentStatus.Incomplete, "missing facts for: " + Ids(triggers.Where(t => t.Result == TriState.Unknown).Select(t => t.Trigger.Id))));
            return Incomplete(IssuerLayer, "human review triggers could not be evaluated", audits)
                with
            { ConfigurationVersion = version, HardEligibilityPass = true, RiskScore = score, RiskFlags = flags };
        }

        var reasons = triggers.Where(t => t.Result == TriState.True).Select(t => $"{t.Trigger.Id}: {t.Trigger.Reason}").ToList();
        if (riskTriggered)
        {
            reasons.Insert(0, $"RISK_THRESHOLD: score {score} >= {config.RiskThreshold}");
        }

        audits.Add(new(TriageComponent.HumanReviewTrigger, ComponentStatus.Evaluated, reasons.Count == 0 ? "no trigger fired" : $"{reasons.Count} trigger(s) fired"));
        if (reasons.Count > 0)
        {
            audits.Add(new(TriageComponent.RoutingPolicy, ComponentStatus.NotEvaluated, "human review decided"));
            return Complete(TriageOutcome.RouteToHuman, "human review trigger", version, audits) with
            {
                HardEligibilityPass = true,
                RiskScore = score,
                RiskFlags = flags,
                HumanReviewTriggered = true,
                HumanReviewReason = string.Join("; ", reasons),
            };
        }

        // 4. Routing policy: first match wins; an unknown before it makes the order undecidable.
        foreach (var rule in config.Routing)
        {
            switch (rule.Condition.Evaluate(facts))
            {
                case TriState.True:
                    audits.Add(new(TriageComponent.RoutingPolicy, ComponentStatus.Evaluated, $"rule '{rule.Id}' matched"));
                    return Complete(rule.Outcome, "routing policy", version, audits) with
                    {
                        HardEligibilityPass = true,
                        RiskScore = score,
                        RiskFlags = flags,
                        RoutingRuleId = rule.Id,
                    };
                case TriState.Unknown:
                    audits.Add(new(TriageComponent.RoutingPolicy, ComponentStatus.Incomplete, $"missing facts for rule '{rule.Id}'"));
                    return Incomplete(IssuerLayer, "routing policy could not be decided", audits)
                        with
                    { ConfigurationVersion = version, HardEligibilityPass = true, RiskScore = score, RiskFlags = flags };
            }
        }

        audits.Add(new(TriageComponent.RoutingPolicy, ComponentStatus.Incomplete, "no routing rule matched"));
        return Incomplete(IssuerLayer, "no routing policy rule matched", audits)
            with
        { ConfigurationVersion = version, HardEligibilityPass = true, RiskScore = score, RiskFlags = flags };
    }

    private static IssuerTriageEvaluation Complete(TriageOutcome outcome, string decidedBy, string version, IReadOnlyList<ComponentAudit> audits) =>
        new(true, false, outcome, IssuerLayer, $"{outcome} decided by {decidedBy}", null, null, null, [], false, null, version, audits);

    private static IssuerTriageEvaluation Incomplete(string layer, string detail, IReadOnlyList<ComponentAudit> audits) =>
        new(false, false, null, layer, detail, null, null, null, [], true, $"{IncompletePrefix}: {detail}", null, audits);

    private static ComponentAudit[] NotEvaluatedAll(string why) =>
        NotEvaluated([TriageComponent.HardEligibility, TriageComponent.RiskScoring, TriageComponent.HumanReviewTrigger, TriageComponent.RoutingPolicy], why);

    private static ComponentAudit[] NotEvaluated(TriageComponent[] components, string why) =>
        components.Select(c => new ComponentAudit(c, ComponentStatus.NotEvaluated, why)).ToArray();

    private static string Ids(IEnumerable<string> ids) => string.Join(", ", ids);

    private sealed record ParsedRule(string Id, Condition Condition, TriageOutcome Outcome);

    private sealed record ParsedFactor(string Id, Condition Condition, decimal Weight);

    private sealed record ParsedTrigger(string Id, Condition Condition, string Reason);

    private sealed record ParsedEligibility(string Id, Condition Condition, TriageOutcome OutcomeWhenNotMet);

    private sealed record ParsedConfiguration(
        IReadOnlyList<ParsedEligibility> HardEligibility,
        IReadOnlyList<ParsedFactor> RiskFactors,
        decimal RiskThreshold,
        IReadOnlyList<ParsedTrigger> HumanReview,
        IReadOnlyList<ParsedRule> Routing)
    {
        public static ParsedConfiguration? TryParse(BankTriageConfiguration config, out string problem)
        {
            problem = "";
            if (config.HardEligibility is null || config.RiskScoring?.Factors is null || config.HumanReviewTriggers is null || config.RoutingPolicy is null)
            {
                problem = "every component must be present (an empty list is an explicit choice)";
                return null;
            }

            var ids = config.HardEligibility.Select(r => r.Id)
                .Concat(config.RiskScoring.Factors.Select(f => f.Id))
                .Concat(config.HumanReviewTriggers.Select(t => t.Id))
                .Concat(config.RoutingPolicy.Select(r => r.Id))
                .ToArray();
            if (ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > MaxRuleIdLength) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            {
                problem = $"rule ids must be unique and 1-{MaxRuleIdLength} characters";
                return null;
            }

            var weights = config.RiskScoring.Factors.Select(f => Math.Abs(f.Weight)).Sum();
            if (weights > MaxRiskScore || config.RiskScoring.Factors.Any(f => decimal.Round(f.Weight, 4) != f.Weight))
            {
                problem = "risk weights must have at most 4 decimals and a total magnitude of at most 9999.9999";
                return null;
            }

            var errors = new List<string>();
            Condition? Parse(string id, string json)
            {
                var result = ConditionParser.Parse(json);
                if (result.IsFailure)
                {
                    errors.Add($"'{id}': {result.Error.Message}");
                    return null;
                }

                return result.Value;
            }

            var eligibility = config.HardEligibility.Select(r => new ParsedEligibility(r.Id, Parse(r.Id, r.ConditionJson)!, r.OutcomeWhenNotMet)).ToArray();
            var factors = config.RiskScoring.Factors.Select(f => new ParsedFactor(f.Id, Parse(f.Id, f.ConditionJson)!, f.Weight)).ToArray();
            var triggers = config.HumanReviewTriggers.Select(t => new ParsedTrigger(t.Id, Parse(t.Id, t.ConditionJson)!, t.Reason)).ToArray();
            var routing = config.RoutingPolicy.Select(r => new ParsedRule(r.Id, Parse(r.Id, r.ConditionJson)!, r.Outcome)).ToArray();

            if (errors.Count > 0)
            {
                problem = string.Join("; ", errors);
                return null;
            }

            return new ParsedConfiguration(eligibility, factors, config.RiskScoring.HumanReviewThreshold, triggers, routing);
        }
    }
}
