using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Infrastructure.Persistence.Queries;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using MediatR;

namespace Chargeback.Api.Features.Intake.GetDispute;

[RequirePermission(Permissions.ViewCases)]
public sealed record GetDisputeQuery(Guid DisputeId) : IQuery<DisputeDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Dispute, DisputeId);
}

/// <summary>The full gate trail is for authorized analysts (common guide §6 Act 2), not bank users.</summary>
[RequirePermission(Permissions.ViewCases)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record GetDisputeGatesQuery(Guid DisputeId) : IQuery<IReadOnlyList<GateResultDto>>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Dispute, DisputeId);
}

internal sealed class GetDisputeHandler(IDapperQueryService db) : IRequestHandler<GetDisputeQuery, Result<DisputeDto>>
{
    private const string Sql = """
        SELECT id, bank_id, cardholder_reference, card_number_masked, transaction_date, transaction_amount,
               currency_code, acquirer_reference_number, merchant_name, intake_channel, status, created_at, updated_at
        FROM chargeback_diagram.disputes
        WHERE id = @DisputeId
        """;

    public async Task<Result<DisputeDto>> Handle(GetDisputeQuery request, CancellationToken cancellationToken)
    {
        var row = await db.QuerySingleOrDefaultAsync<DisputeRow>(Sql, new { request.DisputeId }, cancellationToken);
        return row is null ? Errors.ResourceNotFound : row.ToDto();
    }

    private sealed class DisputeRow
    {
        public Guid Id { get; set; }

        public Guid BankId { get; set; }

        public string? CardholderReference { get; set; }

        public string? CardNumberMasked { get; set; }

        public DateTimeOffset? TransactionDate { get; set; }

        public decimal? TransactionAmount { get; set; }

        public string? CurrencyCode { get; set; }

        public string? AcquirerReferenceNumber { get; set; }

        public string? MerchantName { get; set; }

        public string IntakeChannel { get; set; } = "";

        public string Status { get; set; } = "";

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public DisputeDto ToDto() => new(
            Id,
            BankId,
            CardholderReference,
            CardNumberMasked,
            TransactionDate,
            TransactionAmount,
            CurrencyCode?.Trim(),
            AcquirerReferenceNumber,
            MerchantName,
            Enum.Parse<IntakeChannel>(IntakeChannel, ignoreCase: true),
            Status,
            CreatedAt,
            UpdatedAt);
    }
}

internal sealed class GetDisputeGatesHandler(IDapperQueryService db) : IRequestHandler<GetDisputeGatesQuery, Result<IReadOnlyList<GateResultDto>>>
{
    private const string Sql = """
        SELECT gate_number, gate_name, passed, flag_reason, checked_at
        FROM chargeback_diagram.gate_results
        WHERE dispute_id = @DisputeId
        ORDER BY gate_number
        """;

    public async Task<Result<IReadOnlyList<GateResultDto>>> Handle(GetDisputeGatesQuery request, CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<GateRow>(Sql, new { request.DisputeId }, cancellationToken);
        return Result.Success<IReadOnlyList<GateResultDto>>(
            rows.Select(r => new GateResultDto(r.GateNumber, r.GateName, r.Passed, r.FlagReason, r.CheckedAt, GateExecutionStatuses.FromStored(r.Passed, r.FlagReason))).ToArray());
    }

    private sealed class GateRow
    {
        public int GateNumber { get; set; }

        public string GateName { get; set; } = "";

        public bool? Passed { get; set; }

        public string? FlagReason { get; set; }

        public DateTimeOffset? CheckedAt { get; set; }
    }
}
