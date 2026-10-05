using Chargeback.Api.Features.Cases.Contracts;
using Chargeback.Api.Features.Documents.Contracts;
using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Api.Features.Triage.Contracts;

namespace Chargeback.Api.Features.Review.Contracts;

public sealed record ReviewQueueItemDto(
    Guid CaseId,
    string CaseReference,
    Guid BankId,
    string Status,
    string? Priority,
    DateOnly? FilingDeadlineDate,
    int? DaysRemaining,
    string? HumanReviewReason);

/// <summary>Everything the analyst needs in one read (common guide §6 Act 5).</summary>
public sealed record ReviewWorkspaceDto(
    CaseDetailDto Case,
    DisputeDto Dispute,
    IReadOnlyList<GateResultDto> Gates,
    IReadOnlyList<TriageResultDto> Triage,
    IReadOnlyList<DocumentSlotDto> DocumentChecklist,
    AdvisoryTextDto? AiSummary,
    IReadOnlyList<CaseTimelineEntryDto> Activity);

public enum ReviewDecision
{
    Approve,
    Reject,
}

/// <summary>
/// Human Review decision (common guide §6 Act 5). <c>Rationale</c> (the analyst's notes) is required and audited.
/// <c>ReasonCodeId</c>: on <c>Approve</c>, the id of the case's deterministic derived reason code, sent back as explicit
/// confirmation (it must match; neither the analyst nor AI can substitute a code); omit it on <c>Reject</c>.
/// <c>ExpectedVersion</c>: the case <c>version</c> last read (optimistic concurrency).
/// </summary>
public sealed record ReviewDecisionRequest(ReviewDecision? Decision, string? Rationale, Guid? ReasonCodeId, uint? ExpectedVersion);

/// <summary>The recorded decision (a <c>case_review_decisions</c> row) and the updated case.</summary>
public sealed record ReviewDecisionResponse(
    Guid DecisionId,
    Guid CaseId,
    ReviewDecision Decision,
    Guid? ReasonCodeId,
    string Rationale,
    Guid ReviewedBy,
    DateTimeOffset ReviewedAt,
    CaseDetailDto Case);
