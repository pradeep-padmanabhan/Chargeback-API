using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.Api.Features.Triage.Contracts;

/// <summary><c>Incomplete</c>: required rules/configuration/facts were missing; routed for manual attention with no outcome.</summary>
public enum TriageEvaluationStatus
{
    Complete,
    Incomplete,
}

/// <summary>
/// One <c>triage_results</c> row. Produced by the deterministic SchemeRulesEngine and IssuerTriageEngine;
/// the outcome is a recorded workflow recommendation — it never executes a refund or filing — and is never
/// produced by AI. <c>TriageLayer</c> is the layer that decided (<c>SCHEME_RULES</c> or <c>ISSUER_TRIAGE</c>);
/// <c>RoutingPolicyOutcome</c> holds the matched routing rule id. <c>RiskFlags</c> are the fired risk factor ids.
/// </summary>
/// <summary>Reads a case's triage evaluations, newest first (Triage endpoint and Human Review workspace).</summary>
public interface ITriageResultReader
{
    Task<IReadOnlyList<TriageResultDto>> ReadForCaseAsync(Guid caseId, CancellationToken cancellationToken);
}

public sealed record TriageResultDto(
    Guid Id,
    string? TriageLayer,
    bool? HardEligibilityPass,
    string? RoutingPolicyOutcome,
    decimal? RiskScore,
    IReadOnlyList<string> RiskFlags,
    bool HumanReviewTriggered,
    string? HumanReviewReason,
    TriageOutcome? Outcome,
    DateTimeOffset CreatedAt,
    TriageEvaluationStatus EvaluationStatus);
