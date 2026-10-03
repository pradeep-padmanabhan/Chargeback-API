using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Paging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Cases.Contracts;
using Chargeback.Api.Features.Documents.Contracts;
using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Api.Features.Review.Contracts;
using Chargeback.Api.Features.Triage.Contracts;
using Chargeback.Infrastructure.Ai;
using Chargeback.Infrastructure.Persistence.Queries;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Paging;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using Dapper;
using MediatR;

namespace Chargeback.Api.Features.Review.Workspace;

/// <summary>The review queue: cases in UNDER_REVIEW within the caller's bank scope (taken into review, not yet decided).</summary>
[RequirePermission(Permissions.ReviewCase)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record GetReviewQueueQuery(PageRequest Page) : IQuery<PagedResult<ReviewQueueItemDto>>, IScopeFilteredRequest, IPagedRequest
{
    /// <summary><c>createdAt</c> is the case's creation time.</summary>
    public static readonly SortMap Sorts = new(
        "c.id", ("createdAt", "c.created_at"), ("caseReference", "c.case_reference"), ("priority", "c.priority"), ("filingDeadlineDate", "c.filing_deadline_date"));

    public SortMap Sort => Sorts;
}

/// <summary>Everything the analyst needs to decide, in one read. The AI summary is stored and advisory; reading never generates it.</summary>
[RequirePermission(Permissions.ReviewCase)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record GetReviewWorkspaceQuery(Guid CaseId) : IQuery<ReviewWorkspaceDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

internal sealed class GetReviewQueueHandler(IDapperQueryService db, ICurrentUser currentUser, ISchemeCalendar calendar, TimeProvider timeProvider)
    : IRequestHandler<GetReviewQueueQuery, Result<PagedResult<ReviewQueueItemDto>>>
{
    private const string From = """
        FROM chargeback_diagram.cases c
        JOIN chargeback_diagram.disputes d ON d.id = c.dispute_id
        """;

    private const string Where = "WHERE d.bank_id = ANY(@BankIds) AND c.status = 'UNDER_REVIEW'";

    // Why the case needs review comes from its latest triage evaluation, if any.
    private const string LatestTriage = """
        LEFT JOIN LATERAL (
          SELECT tr.human_review_reason FROM chargeback_diagram.triage_results tr
          WHERE tr.case_id = c.id ORDER BY tr.created_at DESC, tr.id DESC LIMIT 1) t ON true
        """;

    public async Task<Result<PagedResult<ReviewQueueItemDto>>> Handle(GetReviewQueueQuery request, CancellationToken cancellationToken)
    {
        var page = await db.QueryPageAsync<Row>(
            $"SELECT count(*) {From} {Where}",
            $"""
            SELECT c.id AS case_id, c.case_reference, d.bank_id, c.status, c.priority, c.filing_deadline_date, t.human_review_reason
            {From}
            {LatestTriage}
            {Where}
            {request.Sort.OrderBy(request.Page)}
            LIMIT @Limit OFFSET @Offset
            """,
            new DynamicParameters(new { BankIds = currentUser.BankScopes.ToArray() }),
            request.Page,
            cancellationToken);

        var now = timeProvider.GetUtcNow();
        return new PagedResult<ReviewQueueItemDto>(
            page.Items.Select(r => new ReviewQueueItemDto(
                r.CaseId, r.CaseReference, r.BankId, r.Status, r.Priority, r.FilingDeadlineDate,
                calendar.DaysUntil(r.FilingDeadlineDate, now), r.HumanReviewReason)).ToArray(),
            page.Page,
            page.PageSize,
            page.TotalCount);
    }

    private sealed class Row
    {
        public Guid CaseId { get; set; }

        public string CaseReference { get; set; } = "";

        public Guid BankId { get; set; }

        public string Status { get; set; } = "";

        public string? Priority { get; set; }

        public DateOnly? FilingDeadlineDate { get; set; }

        public string? HumanReviewReason { get; set; }
    }
}

internal sealed class GetReviewWorkspaceHandler(
    ICaseDetailReader cases,
    IDisputeReader disputes,
    ITriageResultReader triage,
    IDocumentChecklistReader documents,
    ICaseTimelineReader timeline,
    IDapperQueryService db)
    : IRequestHandler<GetReviewWorkspaceQuery, Result<ReviewWorkspaceDto>>
{
    // The stored summary plus the first successful TriageSummary invocation for the case, which produced it
    // (written once). ADR-0108 proposes a direct link column; until then the log is matched by case and capability.
    private const string SummarySql = """
        SELECT c.ai_summary AS text, l.model_name, l.prompt_template_id, l.created_at AS generated_at
        FROM chargeback_diagram.cases c
        LEFT JOIN LATERAL (
          SELECT a.model_name, a.prompt_template_id, a.created_at
          FROM chargeback_diagram.ai_decision_logs a
          WHERE a.case_id = c.id AND a.capability_name = @Capability AND a.parsed_output IS NOT NULL
          ORDER BY a.created_at, a.id
          LIMIT 1) l ON true
        WHERE c.id = @CaseId
        """;

    public async Task<Result<ReviewWorkspaceDto>> Handle(GetReviewWorkspaceQuery request, CancellationToken cancellationToken)
    {
        if (await cases.ReadAsync(request.CaseId, cancellationToken) is not { } detail)
        {
            return Errors.ResourceNotFound;
        }

        var dispute = (await disputes.ReadAsync(detail.DisputeId, cancellationToken))!;
        var summary = await db.QuerySingleOrDefaultAsync<SummaryRow>(
            SummarySql, new { request.CaseId, Capability = AiCapabilities.TriageSummary }, cancellationToken);

        return new ReviewWorkspaceDto(
            detail,
            dispute,
            await disputes.ReadGatesAsync(detail.DisputeId, cancellationToken),
            await triage.ReadForCaseAsync(request.CaseId, cancellationToken),
            await documents.ReadForCaseAsync(request.CaseId, cancellationToken),
            summary?.Text is { } text ? new AdvisoryTextDto(text, true, summary.ModelName, summary.PromptTemplateId, summary.GeneratedAt) : null,
            await timeline.ReadAsync(request.CaseId, cancellationToken));
    }

    private sealed class SummaryRow
    {
        public string? Text { get; set; }

        public string? ModelName { get; set; }

        public string? PromptTemplateId { get; set; }

        public DateTimeOffset? GeneratedAt { get; set; }
    }
}
