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

/// <summary>Rationale persistence requires ADR-0101 approval.</summary>
public sealed record ReviewDecisionRequest(ReviewDecision Decision, string Rationale);

public sealed record ReviewDecisionResponse(Guid CaseId, ReviewDecision Decision, Guid ReviewedBy, DateTimeOffset ReviewedAt);
