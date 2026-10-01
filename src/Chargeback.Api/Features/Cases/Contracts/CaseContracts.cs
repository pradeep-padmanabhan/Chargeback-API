using System.Text.Json;
using Chargeback.Api.Common.Security;
using Chargeback.SharedKernel.Security;

namespace Chargeback.Api.Features.Cases.Contracts;

/// <summary>Approved <c>cases.status</c> values (ADR-0110, enforced by CHECK constraint in migration 0003).</summary>
public static class CaseStatuses
{
    public const string New = "NEW";
    public const string Flagged = "FLAGGED";
    public const string UnderReview = "UNDER_REVIEW";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Filed = "FILED";
    public const string Closed = "CLOSED";

    public static readonly IReadOnlyList<string> All = [New, Flagged, UnderReview, Approved, Rejected, Filed, Closed];

    public static bool IsValid(string? status) => status is not null && All.Contains(status, StringComparer.Ordinal);
}

/// <summary>
/// Case lifecycle action names (common guide v1.4 §3.2). START_REVIEW, FLAG, UNFLAG and CLOSE go through
/// <c>POST /cases/{id}/transitions</c>. APPROVE and REJECT go through <c>/review/decision</c>, and FILE through
/// <c>/filings/{id}/confirmation</c>; they appear in <c>validActions</c> but are refused by <c>/transitions</c>.
/// </summary>
public static class CaseActions
{
    public const string StartReview = "START_REVIEW";
    public const string Flag = "FLAG";
    public const string Unflag = "UNFLAG";
    public const string Close = "CLOSE";
    public const string Approve = "APPROVE";
    public const string Reject = "REJECT";
    public const string File = "FILE";

    public static readonly IReadOnlyList<string> All = [StartReview, Flag, Unflag, Close, Approve, Reject, File];

    /// <summary>Actions routed to <c>POST /cases/{id}/transitions</c>.</summary>
    public static readonly IReadOnlyList<string> Transitions = [StartReview, Flag, Unflag, Close];

    /// <summary>Actions that need a rationale on <c>/transitions</c>.</summary>
    public static readonly IReadOnlyList<string> RationaleRequired = [Close];

    public static bool IsKnown(string? action) => action is not null && All.Contains(action, StringComparer.Ordinal);

    public static bool IsTransition(string action) => Transitions.Contains(action, StringComparer.Ordinal);
}

/// <summary>Who may perform a status transition.</summary>
public enum TransitionActor
{
    /// <summary>Processor or admin user with UPDATE_CASE_STATUS, through <c>POST /cases/{id}/transitions</c>.</summary>
    Analyst,

    /// <summary>Admin user with UPDATE_CASE_STATUS, through <c>POST /cases/{id}/transitions</c>.</summary>
    Admin,

    /// <summary>The Human Review decision (Phase 9, REVIEW_CASE): approve/reject with rationale. Never via /transitions.</summary>
    ReviewDecision,

    /// <summary>Explicit human filing confirmation (Phase 10, SUBMIT_MASTERCOM). Never via /transitions.</summary>
    FilingConfirmation,
}

public sealed record CaseStatusTransition(string From, string Action, string To, TransitionActor Actor, string Source);

/// <summary>
/// Allowed case status transitions (common guide v1.4 §3.2, approved), enforced in the application layer (the
/// database only constrains the value set). FLAG and UNFLAG are recognised action names but have no approved
/// transitions yet (ADR-0110 open question), so they are never valid.
/// Initial status on creation: NEW (all ten gates passed) or FLAGGED (otherwise).
/// </summary>
public static class CaseStatusTransitions
{
    public static readonly IReadOnlyList<CaseStatusTransition> All =
    [
        new(CaseStatuses.New, CaseActions.StartReview, CaseStatuses.UnderReview, TransitionActor.Analyst, "Act 5: analyst takes the case into review"),
        new(CaseStatuses.Flagged, CaseActions.StartReview, CaseStatuses.UnderReview, TransitionActor.Analyst, "Act 2/5: flagged case picked up from the analyst queue"),
        new(CaseStatuses.UnderReview, CaseActions.Approve, CaseStatuses.Approved, TransitionActor.ReviewDecision, "Act 5: analyst approves (Phase 9)"),
        new(CaseStatuses.UnderReview, CaseActions.Reject, CaseStatuses.Rejected, TransitionActor.ReviewDecision, "Act 5: analyst rejects (Phase 9)"),
        new(CaseStatuses.Approved, CaseActions.File, CaseStatuses.Filed, TransitionActor.FilingConfirmation, "Act 6: mandatory human filing confirmation (Phase 10)"),
        new(CaseStatuses.Rejected, CaseActions.Close, CaseStatuses.Closed, TransitionActor.Analyst, "Act 5: rejection path completed"),
        new(CaseStatuses.Filed, CaseActions.Close, CaseStatuses.Closed, TransitionActor.Admin, "Act 7: admin closes a filed case"),
    ];

    public static CaseStatusTransition? Find(string from, string action) =>
        All.FirstOrDefault(t => t.From == from && t.Action == action);

    /// <summary>Actions the user may take on a case in <paramref name="status"/>; the server-computed <c>validActions</c>.</summary>
    public static IReadOnlyList<string> ValidActions(string status, ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return All.Where(t => t.From == status && IsPermitted(t.Actor, user)).Select(t => t.Action).Distinct().ToArray();
    }

    public static bool IsPermitted(TransitionActor actor, ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var staff = user.UserType is UserType.Processor or UserType.Admin;
        return actor switch
        {
            TransitionActor.Analyst => staff && user.HasPermission(Permissions.UpdateCaseStatus),
            TransitionActor.Admin => user.UserType == UserType.Admin && user.HasPermission(Permissions.UpdateCaseStatus),
            TransitionActor.ReviewDecision => staff && user.HasPermission(Permissions.ReviewCase),
            TransitionActor.FilingConfirmation => staff && user.HasPermission(Permissions.SubmitMastercom),
            _ => false,
        };
    }
}

/// <summary>
/// Case row for analyst lists. <c>DaysRemaining</c> is calculated by the server at read time from
/// <c>FilingDeadlineDate</c> (null until the calendar is approved, ADR-0115/0122); clients display it, never compute it.
/// </summary>
public sealed record CaseSummaryDto(
    Guid Id,
    Guid DisputeId,
    Guid BankId,
    string CaseReference,
    string Status,
    string? Priority,
    Guid? AssignedTo,
    DateOnly? FilingDeadlineDate,
    int? DaysRemaining,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Reason code chosen by deterministic, versioned scheme rules — never by AI.</summary>
public sealed record DerivedReasonCodeDto(Guid Id, string Code, string Description, string? Category);

public sealed record HumanReviewDto(string? Verdict, Guid? ReviewedBy, DateTimeOffset? ReviewedAt);

/// <summary>
/// Case detail. Send <c>Version</c> back as <c>expectedVersion</c> on <c>/transitions</c>, or as <c>If-Match</c> on
/// <c>/assignment</c> (it is also the <c>ETag</c>). <c>ValidActions</c> are the lifecycle actions the caller may take now.
/// </summary>
public sealed record CaseDetailDto(
    Guid Id,
    Guid DisputeId,
    Guid BankId,
    string CaseReference,
    string Status,
    string? Priority,
    Guid? AssignedTo,
    DerivedReasonCodeDto? DerivedReasonCode,
    DateOnly? ClockStartDate,
    DateOnly? FilingDeadlineDate,
    int? DaysRemaining,
    string? SchemeFunctionCode,
    HumanReviewDto? HumanReview,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version,
    IReadOnlyList<string> ValidActions);

/// <summary>One entry of the append-only case timeline: a <c>domain_events</c> row for this case (ADR-0109).</summary>
public sealed record CaseTimelineEntryDto(Guid EventId, string EventType, DateTimeOffset OccurredAt, JsonElement? Data);

/// <summary>
/// Lifecycle action on <c>POST /cases/{id}/transitions</c>. <c>Rationale</c> is recorded on the timeline (required for
/// CLOSE). <c>ExpectedVersion</c> is the case <c>version</c> the caller last read (optimistic concurrency).
/// </summary>
public sealed record TransitionCaseRequest(string Action, string? Rationale, uint? ExpectedVersion);

/// <summary><c>AssignedTo = null</c> unassigns.</summary>
public sealed record UpdateCaseAssignmentRequest(Guid? AssignedTo);

/// <summary>AI-generated text shown to analysts. Always advisory.</summary>
public sealed record AdvisoryTextDto(string Text, bool Advisory, string? ModelName, string? PromptTemplateId, DateTimeOffset? GeneratedAt);
