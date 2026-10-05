using System.Text.Json;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Paging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Cases.Contracts;
using Chargeback.Api.Features.Triage.Contracts;
using Chargeback.Infrastructure.Persistence.Queries;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Paging;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using Dapper;
using FluentValidation;
using MediatR;

namespace Chargeback.Api.Features.Cases.GetCases;

/// <summary>Analyst case list (the analyst queue is e.g. <c>status=FLAGGED</c>), filtered to the caller's bank scope.</summary>
[RequirePermission(Permissions.ViewCases)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record ListCasesQuery(string? Status, Guid? BankId, PageRequest Page) : IQuery<PagedResult<CaseSummaryDto>>, IScopeFilteredRequest, IPagedRequest
{
    public static readonly SortMap Sorts = new(
        "c.id",
        ("createdAt", "c.created_at"),
        ("updatedAt", "c.updated_at"),
        ("caseReference", "c.case_reference"),
        ("status", "c.status"),
        ("priority", "c.priority"),
        ("filingDeadlineDate", "c.filing_deadline_date"));

    public SortMap Sort => Sorts;
}

[RequirePermission(Permissions.ViewCases)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record GetCaseQuery(Guid CaseId) : IQuery<CaseDetailDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

/// <summary>Append-only timeline = <c>domain_events</c> filtered by case id (ADR-0109). Analysts only (internal detail).</summary>
[RequirePermission(Permissions.ViewCases)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record GetCaseTimelineQuery(Guid CaseId) : IQuery<IReadOnlyList<CaseTimelineEntryDto>>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

public sealed class ListCasesValidator : AbstractValidator<ListCasesQuery>
{
    public ListCasesValidator() =>
        RuleFor(x => x.Status)
            .Must(CaseStatuses.IsValid)
            .When(x => x.Status is not null)
            .WithMessage($"Status must be one of: {string.Join(", ", CaseStatuses.All)}.")
            .OverridePropertyName("status");
}

internal sealed class ListCasesHandler(IDapperQueryService db, ICurrentUser currentUser, ISchemeCalendar calendar, TimeProvider timeProvider)
    : IRequestHandler<ListCasesQuery, Result<PagedResult<CaseSummaryDto>>>
{
    public async Task<Result<PagedResult<CaseSummaryDto>>> Handle(ListCasesQuery request, CancellationToken cancellationToken)
    {
        var filter = """
            FROM chargeback_diagram.cases c
            JOIN chargeback_diagram.disputes d ON d.id = c.dispute_id
            WHERE d.bank_id = ANY(@BankIds)
            """;
        var parameters = new DynamicParameters(new { BankIds = currentUser.BankScopes.ToArray() });
        if (request.Status is not null)
        {
            filter += " AND c.status = @Status";
            parameters.Add("Status", request.Status);
        }

        if (request.BankId is not null)
        {
            // Narrows within the caller's scope; a bank outside scope simply yields no rows.
            filter += " AND d.bank_id = @BankId";
            parameters.Add("BankId", request.BankId);
        }

        var page = await db.QueryPageAsync<SummaryRow>(
            $"SELECT count(*) {filter}",
            $"""
            SELECT c.id, c.dispute_id, d.bank_id, c.case_reference, c.status, c.priority, c.assigned_to,
                   c.filing_deadline_date, c.created_at, c.updated_at
            {filter}
            {request.Sort.OrderBy(request.Page)}
            LIMIT @Limit OFFSET @Offset
            """,
            parameters,
            request.Page,
            cancellationToken);

        var now = timeProvider.GetUtcNow();
        return new PagedResult<CaseSummaryDto>(
            page.Items.Select(r => new CaseSummaryDto(
                r.Id, r.DisputeId, r.BankId, r.CaseReference, r.Status, r.Priority, r.AssignedTo, r.FilingDeadlineDate,
                calendar.DaysUntil(r.FilingDeadlineDate, now), r.CreatedAt, r.UpdatedAt)).ToArray(),
            page.Page,
            page.PageSize,
            page.TotalCount);
    }

    private sealed class SummaryRow
    {
        public Guid Id { get; set; }

        public Guid DisputeId { get; set; }

        public Guid BankId { get; set; }

        public string CaseReference { get; set; } = "";

        public string Status { get; set; } = "";

        public string? Priority { get; set; }

        public Guid? AssignedTo { get; set; }

        public DateOnly? FilingDeadlineDate { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}

internal sealed class CaseDetailReader(IDapperQueryService db, ISchemeCalendar calendar, TimeProvider timeProvider, ICurrentUser currentUser) : ICaseDetailReader
{
    private const string Sql = """
        SELECT c.id, c.dispute_id, d.bank_id, c.case_reference, c.status, c.priority, c.assigned_to,
               c.derived_reason_code AS reason_code_id, rc.code AS reason_code, rc.description AS reason_code_description,
               rc.category AS reason_code_category, c.clock_start_date, c.filing_deadline_date, c.scheme_function_code,
               c.human_review_verdict, c.human_reviewed_by, c.human_reviewed_at, c.created_at, c.updated_at,
               c.xmin::text::bigint AS version
        FROM chargeback_diagram.cases c
        JOIN chargeback_diagram.disputes d ON d.id = c.dispute_id
        LEFT JOIN chargeback_diagram.scheme_reason_codes rc ON rc.id = c.derived_reason_code
        WHERE c.id = @CaseId
        """;

    public async Task<CaseDetailDto?> ReadAsync(Guid caseId, CancellationToken cancellationToken)
    {
        var r = await db.QuerySingleOrDefaultAsync<DetailRow>(Sql, new { CaseId = caseId }, cancellationToken);
        if (r is null)
        {
            return null;
        }

        var reasonCode = r.ReasonCodeId is { } id ? new DerivedReasonCodeDto(id, r.ReasonCode!, r.ReasonCodeDescription!, r.ReasonCodeCategory) : null;
        var review = r.HumanReviewVerdict is null && r.HumanReviewedBy is null ? null : new HumanReviewDto(r.HumanReviewVerdict, r.HumanReviewedBy, r.HumanReviewedAt);
        return new CaseDetailDto(
            r.Id, r.DisputeId, r.BankId, r.CaseReference, r.Status, r.Priority, r.AssignedTo, reasonCode, r.ClockStartDate,
            r.FilingDeadlineDate, calendar.DaysUntil(r.FilingDeadlineDate, timeProvider.GetUtcNow()), r.SchemeFunctionCode, review,
            r.CreatedAt, r.UpdatedAt, (uint)r.Version, CaseStatusTransitions.ValidActions(r.Status, currentUser));
    }

    private sealed class DetailRow
    {
        public Guid Id { get; set; }

        public Guid DisputeId { get; set; }

        public Guid BankId { get; set; }

        public string CaseReference { get; set; } = "";

        public string Status { get; set; } = "";

        public string? Priority { get; set; }

        public Guid? AssignedTo { get; set; }

        public Guid? ReasonCodeId { get; set; }

        public string? ReasonCode { get; set; }

        public string? ReasonCodeDescription { get; set; }

        public string? ReasonCodeCategory { get; set; }

        public DateOnly? ClockStartDate { get; set; }

        public DateOnly? FilingDeadlineDate { get; set; }

        public string? SchemeFunctionCode { get; set; }

        public string? HumanReviewVerdict { get; set; }

        public Guid? HumanReviewedBy { get; set; }

        public DateTimeOffset? HumanReviewedAt { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public long Version { get; set; }
    }
}

internal sealed class GetCaseHandler(ICaseDetailReader reader) : IRequestHandler<GetCaseQuery, Result<CaseDetailDto>>
{
    public async Task<Result<CaseDetailDto>> Handle(GetCaseQuery request, CancellationToken cancellationToken) =>
        await reader.ReadAsync(request.CaseId, cancellationToken) is { } dto ? dto : Errors.ResourceNotFound;
}

internal sealed class GetCaseTimelineHandler(ICaseTimelineReader timeline) : IRequestHandler<GetCaseTimelineQuery, Result<IReadOnlyList<CaseTimelineEntryDto>>>
{
    public async Task<Result<IReadOnlyList<CaseTimelineEntryDto>>> Handle(GetCaseTimelineQuery request, CancellationToken cancellationToken) =>
        Result.Success(await timeline.ReadAsync(request.CaseId, cancellationToken));
}

internal sealed class CaseTimelineReader(IDapperQueryService db) : ICaseTimelineReader
{
    private const string Sql = """
        SELECT id, event_type, (event_data->'data')::text AS data, created_at
        FROM chargeback_diagram.domain_events
        WHERE case_id = @CaseId
        ORDER BY created_at, id
        """;

    public async Task<IReadOnlyList<CaseTimelineEntryDto>> ReadAsync(Guid caseId, CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<Row>(Sql, new { CaseId = caseId }, cancellationToken);
        return rows.Select(r => new CaseTimelineEntryDto(
            r.Id, r.EventType, r.CreatedAt, r.Data is null ? null : JsonDocument.Parse(r.Data).RootElement.Clone())).ToArray();
    }

    private sealed class Row
    {
        public Guid Id { get; set; }

        public string EventType { get; set; } = "";

        public string? Data { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }
}
