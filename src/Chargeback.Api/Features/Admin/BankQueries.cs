using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Admin.Contracts;
using Chargeback.Infrastructure.Persistence.Queries;
using Chargeback.SharedKernel.Paging;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using Dapper;
using MediatR;

namespace Chargeback.Api.Features.Admin;

// Implemented reads: they exercise the RBAC + bank-scope framework end to end
// (endpoint → MediatR pipeline → handler → PostgreSQL). No chargeback business logic.

[RequirePermission(Permissions.ViewBanks)]
public sealed record ListBanksQuery(PageRequest Page) : IQuery<PagedResult<BankDto>>, IScopeFilteredRequest;

[RequirePermission(Permissions.ViewBanks)]
public sealed record GetBankQuery(Guid BankId) : IQuery<BankDto>, IBankScopedRequest;

/// <summary>Processor users need VIEW_BANK_USERS and an authorized scope for the bank (common guide §3).</summary>
[RequirePermission(Permissions.ViewBankUsers)]
public sealed record ListBankUsersQuery(Guid BankId, PageRequest Page) : IQuery<PagedResult<BankUserDto>>, IBankScopedRequest;

[RequirePermission(Permissions.ManageRoles)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
[NotBankScoped("Permission catalogue is global configuration, not bank-owned data.")]
public sealed record ListPermissionsQuery : IQuery<IReadOnlyList<PermissionDto>>;

internal sealed class ListBanksHandler(IDapperQueryService db, ICurrentUser currentUser)
    : IRequestHandler<ListBanksQuery, Result<PagedResult<BankDto>>>
{
    private const string Filter = "FROM chargeback_diagram.banks b WHERE b.id = ANY(@BankIds)";

    public async Task<Result<PagedResult<BankDto>>> Handle(ListBanksQuery request, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters(new { BankIds = currentUser.BankScopes.ToArray() });
        var page = await db.QueryPageAsync<BankRow>(
            $"SELECT count(*) {Filter}",
            $"SELECT b.id, b.bank_code, b.bank_name, b.country, b.status, b.created_at, b.updated_at {Filter} ORDER BY b.bank_code LIMIT @Limit OFFSET @Offset",
            parameters,
            request.Page,
            cancellationToken);

        return new PagedResult<BankDto>(page.Items.Select(r => r.ToDto()).ToArray(), page.Page, page.PageSize, page.TotalCount);
    }
}

internal sealed class GetBankHandler(IDapperQueryService db) : IRequestHandler<GetBankQuery, Result<BankDto>>
{
    public async Task<Result<BankDto>> Handle(GetBankQuery request, CancellationToken cancellationToken)
    {
        var row = await db.QuerySingleOrDefaultAsync<BankRow>(
            "SELECT id, bank_code, bank_name, country, status, created_at, updated_at FROM chargeback_diagram.banks WHERE id = @BankId",
            new { request.BankId },
            cancellationToken);
        return row is null ? Errors.ResourceNotFound : row.ToDto();
    }
}

internal sealed class ListBankUsersHandler(IDapperQueryService db)
    : IRequestHandler<ListBankUsersQuery, Result<PagedResult<BankUserDto>>>
{
    private const string Filter = "FROM chargeback_diagram.users u WHERE u.bank_id = @BankId AND u.user_type = 'BANK'";

    public async Task<Result<PagedResult<BankUserDto>>> Handle(ListBankUsersQuery request, CancellationToken cancellationToken)
    {
        var page = await db.QueryPageAsync<BankUserRow>(
            $"SELECT count(*) {Filter}",
            $"SELECT u.id, u.bank_id, u.email, u.full_name, u.user_type, u.role_id, u.status, u.created_at, u.updated_at {Filter} ORDER BY u.email, u.id LIMIT @Limit OFFSET @Offset",
            new DynamicParameters(new { request.BankId }),
            request.Page,
            cancellationToken);

        return new PagedResult<BankUserDto>(page.Items.Select(r => r.ToDto()).ToArray(), page.Page, page.PageSize, page.TotalCount);
    }
}

internal sealed class ListPermissionsHandler(IDapperQueryService db)
    : IRequestHandler<ListPermissionsQuery, Result<IReadOnlyList<PermissionDto>>>
{
    public async Task<Result<IReadOnlyList<PermissionDto>>> Handle(ListPermissionsQuery request, CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<PermissionRow>(
            "SELECT id, name, resource, action, description, is_active FROM chargeback_diagram.permissions ORDER BY name",
            null,
            cancellationToken);
        return Result.Success<IReadOnlyList<PermissionDto>>(
            rows.Select(r => new PermissionDto(r.Id, r.Name, r.Resource, r.Action, r.Description, r.IsActive)).ToArray());
    }
}

internal sealed class BankRow
{
    public Guid Id { get; set; }

    public string BankCode { get; set; } = "";

    public string BankName { get; set; } = "";

    public string? Country { get; set; }

    public string Status { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public BankDto ToDto() => new(Id, BankCode, BankName, Country, Status, CreatedAt, UpdatedAt);
}

internal sealed class BankUserRow
{
    public Guid Id { get; set; }

    public Guid BankId { get; set; }

    public string Email { get; set; } = "";

    public string FullName { get; set; } = "";

    public string UserType { get; set; } = "";

    public Guid RoleId { get; set; }

    public string Status { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public BankUserDto ToDto() =>
        new(Id, BankId, Email, FullName, Enum.Parse<UserType>(UserType, true), RoleId, Status, CreatedAt, UpdatedAt);
}

internal sealed class PermissionRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string Resource { get; set; } = "";

    public string Action { get; set; } = "";

    public string? Description { get; set; }

    public bool IsActive { get; set; }
}
