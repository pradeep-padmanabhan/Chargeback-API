using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Paging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Cases.Contracts;
using Chargeback.Api.Features.ClientPortal.Contracts;
using Chargeback.Infrastructure.Persistence.Queries;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Paging;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using Chargeback.SharedKernel.ValueObjects;
using Dapper;
using FluentValidation;
using MediatR;

namespace Chargeback.Api.Features.ClientPortal;

// Bank users only. No permission code: the gate is userType = BANK plus the user's own bank (users.bank_id); BANK users
// hold no permissions (guide §3.1). Another bank's case is 404.

/// <summary>The bank's own cases. Optional <c>status</c> filter (approved case statuses).</summary>
[AllowAnyPlatformUser]
[RestrictToUserTypes(UserType.Bank)]
public sealed record ListPortalCasesQuery(string? Status, PageRequest Page) : IQuery<PagedResult<PortalCaseSummaryDto>>, IScopeFilteredRequest, IPagedRequest
{
    public static readonly SortMap Sorts = new(
        "c.id", ("createdAt", "c.created_at"), ("updatedAt", "c.updated_at"), ("referenceNumber", "c.case_reference"), ("status", "c.status"));

    public SortMap Sort => Sorts;
}

[AllowAnyPlatformUser]
[RestrictToUserTypes(UserType.Bank)]
public sealed record GetPortalCaseQuery(Guid CaseId) : IQuery<PortalCaseDetailDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

public sealed class ListPortalCasesValidator : AbstractValidator<ListPortalCasesQuery>
{
    public ListPortalCasesValidator() =>
        RuleFor(x => x.Status)
            .Must(CaseStatuses.IsValid)
            .When(x => x.Status is not null)
            .WithMessage($"Status must be one of: {string.Join(", ", CaseStatuses.All)}.")
            .OverridePropertyName("status");
}

internal sealed class ListPortalCasesHandler(IDapperQueryService db, ICurrentUser currentUser)
    : IRequestHandler<ListPortalCasesQuery, Result<PagedResult<PortalCaseSummaryDto>>>
{
    public async Task<Result<PagedResult<PortalCaseSummaryDto>>> Handle(ListPortalCasesQuery request, CancellationToken cancellationToken)
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

        var page = await db.QueryPageAsync<PortalCaseRow>(
            $"SELECT count(*) {filter}",
            $"""
            SELECT c.id AS case_id, c.case_reference AS reference_number, c.status, d.transaction_amount AS amount,
                   d.currency_code AS currency, d.merchant_name, c.created_at, c.updated_at
            {filter}
            {request.Sort.OrderBy(request.Page)}
            LIMIT @Limit OFFSET @Offset
            """,
            parameters,
            request.Page,
            cancellationToken);

        return new PagedResult<PortalCaseSummaryDto>(page.Items.Select(r => r.ToDto()).ToArray(), page.Page, page.PageSize, page.TotalCount);
    }
}

internal sealed class GetPortalCaseHandler(IDapperQueryService db) : IRequestHandler<GetPortalCaseQuery, Result<PortalCaseDetailDto>>
{
    private const string CaseSql = """
        SELECT c.id AS case_id, c.case_reference AS reference_number, c.status, d.transaction_amount AS amount,
               d.currency_code AS currency, d.merchant_name, c.created_at, c.updated_at,
               rc.code AS reason_code, rc.description AS reason_description, c.filing_deadline_date AS network_deadline
        FROM chargeback_diagram.cases c
        JOIN chargeback_diagram.disputes d ON d.id = c.dispute_id
        LEFT JOIN chargeback_diagram.scheme_reason_codes rc ON rc.id = c.derived_reason_code
        WHERE c.id = @CaseId
        """;

    // Confirmed, live uploads only: a declared-but-not-uploaded or removed document is not shown to the bank.
    private const string DocumentsSql = """
        SELECT file_name AS name, document_status AS processing_status
        FROM chargeback_diagram.documents
        WHERE case_id = @CaseId AND deleted_at IS NULL AND upload_status = 'UPLOADED'
        ORDER BY uploaded_at DESC, id DESC
        """;

    public async Task<Result<PortalCaseDetailDto>> Handle(GetPortalCaseQuery request, CancellationToken cancellationToken)
    {
        var row = await db.QuerySingleOrDefaultAsync<DetailRow>(CaseSql, new { request.CaseId }, cancellationToken);
        if (row is null)
        {
            return Errors.ResourceNotFound;
        }

        var documents = (await db.QueryAsync<DocumentRow>(DocumentsSql, new { request.CaseId }, cancellationToken))
            .Select(d => new PortalDocumentDto(d.Name, Enum.Parse<DocumentStatus>(d.ProcessingStatus, ignoreCase: true)))
            .ToArray();
        var summary = row.ToDto();
        return new PortalCaseDetailDto(
            summary.CaseId, summary.ReferenceNumber, summary.Status, summary.Amount, summary.Currency, summary.MerchantName,
            summary.CreatedAt, summary.UpdatedAt, row.ReasonCode, row.ReasonDescription, row.NetworkDeadline, documents);
    }

    private sealed class DetailRow : PortalCaseRow
    {
        public string? ReasonCode { get; set; }

        public string? ReasonDescription { get; set; }

        public DateOnly? NetworkDeadline { get; set; }
    }

    private sealed class DocumentRow
    {
        public string Name { get; set; } = "";

        public string ProcessingStatus { get; set; } = "";
    }
}

internal class PortalCaseRow
{
    public Guid CaseId { get; set; }

    public string ReferenceNumber { get; set; } = "";

    public string Status { get; set; } = "";

    public decimal? Amount { get; set; }

    public string? Currency { get; set; }

    public string? MerchantName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public PortalCaseSummaryDto ToDto() =>
        new(CaseId, ReferenceNumber, Status, Amount, Currency?.Trim(), MerchantName, CreatedAt, UpdatedAt);
}
