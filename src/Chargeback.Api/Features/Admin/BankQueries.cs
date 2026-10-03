using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Paging;
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

// Bank and user reads. Processor and admin users only (a bank-admin role is deferred, guide §3.1). A processor sees only
// the banks in its user_bank_scopes rows: a NULL users.bank_id never grants cross-bank access.

/// <summary>Banks within the caller's scope. Optional exact <c>status</c> filter (the bank status vocabulary is open, ADR-0110).</summary>
[RequirePermission(Permissions.ViewBankUsers)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record ListBanksQuery(string? Status, PageRequest Page) : IQuery<PagedResult<BankDto>>, IScopeFilteredRequest, IPagedRequest
{
    public static readonly SortMap Sorts = new(
        "b.id", ("createdAt", "b.created_at"), ("updatedAt", "b.updated_at"), ("bankCode", "b.bank_code"), ("bankName", "b.bank_name"), ("status", "b.status"));

    public SortMap Sort => Sorts;
}

[RequirePermission(Permissions.ViewBankUsers)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record GetBankQuery(Guid BankId) : IQuery<BankDetailDto>, IBankScopedRequest;

/// <summary>Users of a bank (removed ones excluded). Processor users need VIEW_BANK_USERS and an authorized scope for the bank.</summary>
[RequirePermission(Permissions.ViewBankUsers)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record ListBankUsersQuery(Guid BankId, PageRequest Page) : IQuery<PagedResult<BankUserDto>>, IBankScopedRequest, IPagedRequest
{
    public static readonly SortMap Sorts = new(
        "u.id", ("createdAt", "u.created_at"), ("updatedAt", "u.updated_at"), ("email", "u.email"), ("fullName", "u.full_name"), ("status", "u.status"));

    public SortMap Sort => Sorts;
}

[RequirePermission(Permissions.ManageRoles)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
[NotBankScoped("Permission catalogue is global configuration, not bank-owned data.")]
public sealed record ListPermissionsQuery : IQuery<IReadOnlyList<PermissionDto>>;

/// <summary>Every role with its permissions. Any platform user may inspect the roles; nobody can change them here.</summary>
[AllowAnyPlatformUser]
[NotBankScoped("Role catalogue is global configuration, not bank-owned data.")]
public sealed record ListRolesQuery : IQuery<IReadOnlyList<RoleDto>>;

internal sealed class ListBanksHandler(IDapperQueryService db, ICurrentUser currentUser)
    : IRequestHandler<ListBanksQuery, Result<PagedResult<BankDto>>>
{
    public async Task<Result<PagedResult<BankDto>>> Handle(ListBanksQuery request, CancellationToken cancellationToken)
    {
        var filter = "FROM chargeback_diagram.banks b WHERE b.id = ANY(@BankIds)";
        var parameters = new DynamicParameters(new { BankIds = currentUser.BankScopes.ToArray() });
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            filter += " AND b.status = @Status";
            parameters.Add("Status", request.Status.Trim());
        }

        var page = await db.QueryPageAsync<BankRow>(
            $"SELECT count(*) {filter}",
            $"SELECT b.id, b.bank_code, b.bank_name, b.country, b.status, b.created_at, b.updated_at {filter} {request.Sort.OrderBy(request.Page)} LIMIT @Limit OFFSET @Offset",
            parameters,
            request.Page,
            cancellationToken);

        return new PagedResult<BankDto>(page.Items.Select(r => r.ToDto()).ToArray(), page.Page, page.PageSize, page.TotalCount);
    }
}

internal sealed class GetBankHandler(IDapperQueryService db, ICurrentUser currentUser) : IRequestHandler<GetBankQuery, Result<BankDetailDto>>
{
    private const string BankSql = "SELECT id, bank_code, bank_name, country, status, created_at, updated_at FROM chargeback_diagram.banks WHERE id = @BankId";

    private const string UsersByRoleSql = """
        SELECT r.id AS role_id, r.name AS role_name, count(*)::int AS active_users
        FROM chargeback_diagram.users u
        JOIN chargeback_diagram.roles r ON r.id = u.role_id
        WHERE u.bank_id = @BankId AND u.status = 'ACTIVE' AND u.deleted_at IS NULL
        GROUP BY r.id, r.name
        ORDER BY r.name
        """;

    public async Task<Result<BankDetailDto>> Handle(GetBankQuery request, CancellationToken cancellationToken)
    {
        var bank = await db.QuerySingleOrDefaultAsync<BankRow>(BankSql, new { request.BankId }, cancellationToken);
        if (bank is null)
        {
            return Errors.ResourceNotFound;
        }

        var byRole = (await db.QueryAsync<RoleUserCountRow>(UsersByRoleSql, new { request.BankId }, cancellationToken))
            .Select(r => new RoleUserCountDto(r.RoleId, r.RoleName, r.ActiveUsers))
            .ToArray();
        return new BankDetailDto(
            bank.Id, bank.BankCode, bank.BankName, bank.Country, bank.Status, bank.CreatedAt, bank.UpdatedAt,
            byRole.Sum(r => r.ActiveUsers), byRole, [.. currentUser.Permissions.Order(StringComparer.Ordinal)]);
    }

    private sealed class RoleUserCountRow
    {
        public Guid RoleId { get; set; }

        public string RoleName { get; set; } = "";

        public int ActiveUsers { get; set; }
    }
}

internal sealed class ListBankUsersHandler(IDapperQueryService db)
    : IRequestHandler<ListBankUsersQuery, Result<PagedResult<BankUserDto>>>
{
    private const string Filter = "FROM chargeback_diagram.users u WHERE u.bank_id = @BankId AND u.user_type = 'BANK' AND u.deleted_at IS NULL";

    public async Task<Result<PagedResult<BankUserDto>>> Handle(ListBankUsersQuery request, CancellationToken cancellationToken)
    {
        var page = await db.QueryPageAsync<BankUserRow>(
            $"SELECT count(*) {Filter}",
            $"SELECT {BankUserRow.Columns} {Filter} {request.Sort.OrderBy(request.Page)} LIMIT @Limit OFFSET @Offset",
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

internal sealed class ListRolesHandler(IDapperQueryService db) : IRequestHandler<ListRolesQuery, Result<IReadOnlyList<RoleDto>>>
{
    private const string Sql = """
        SELECT r.id, r.name, r.description, r.role_type, r.is_active,
               coalesce(array_agg(p.name ORDER BY p.name) FILTER (WHERE p.name IS NOT NULL), '{}') AS permissions
        FROM chargeback_diagram.roles r
        LEFT JOIN chargeback_diagram.role_permissions rp ON rp.role_id = r.id
        LEFT JOIN chargeback_diagram.permissions p ON p.id = rp.permission_id AND p.is_active
        GROUP BY r.id, r.name, r.description, r.role_type, r.is_active
        ORDER BY r.name
        """;

    public async Task<Result<IReadOnlyList<RoleDto>>> Handle(ListRolesQuery request, CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<RoleRow>(Sql, null, cancellationToken);
        return Result.Success<IReadOnlyList<RoleDto>>(rows
            .Select(r => new RoleDto(r.Id, r.Name, r.Description, Enum.Parse<UserType>(r.RoleType, ignoreCase: true), r.IsActive, r.Permissions))
            .ToArray());
    }

    private sealed class RoleRow
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public string? Description { get; set; }

        public string RoleType { get; set; } = "";

        public bool IsActive { get; set; }

        public string[] Permissions { get; set; } = [];
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
    public const string Columns =
        "u.id, u.bank_id, u.email, u.full_name, u.user_type, u.role_id, u.status, u.invited_at, u.deleted_at, u.created_at, u.updated_at";

    public Guid Id { get; set; }

    public Guid BankId { get; set; }

    public string Email { get; set; } = "";

    public string FullName { get; set; } = "";

    public string UserType { get; set; } = "";

    public Guid RoleId { get; set; }

    public string Status { get; set; } = "";

    public DateTimeOffset? InvitedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public BankUserDto ToDto() =>
        new(Id, BankId, Email, FullName, Enum.Parse<UserType>(UserType, true), RoleId, Status, InvitedAt, DeletedAt, CreatedAt, UpdatedAt);
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
