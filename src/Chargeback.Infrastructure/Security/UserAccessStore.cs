using Chargeback.Infrastructure.Persistence.Queries;
using Chargeback.SharedKernel.Security;

namespace Chargeback.Infrastructure.Security;

/// <summary>Everything the authorization pipeline needs about a user, loaded from the database.</summary>
public sealed record UserAccess(
    Guid UserId,
    string CognitoSub,
    string Email,
    string FullName,
    UserType UserType,
    UserType RoleType,
    Guid RoleId,
    string RoleName,
    bool RoleIsActive,
    Guid? HomeBankId,
    string Status,
    IReadOnlySet<string> Permissions,
    IReadOnlySet<Guid> BankScopes);

public interface IUserAccessStore
{
    /// <returns><c>null</c> when no platform user is provisioned for the Cognito subject.</returns>
    Task<UserAccess?> FindByCognitoSubAsync(string cognitoSub, DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed class UserAccessStore(IDapperQueryService db) : IUserAccessStore
{
    private const string UserSql = """
        SELECT u.id AS user_id, u.cognito_sub, u.email, u.full_name, u.user_type, u.role_id, u.bank_id,
               u.status, r.name AS role_name, r.role_type, r.is_active AS role_is_active
        FROM chargeback_diagram.users u
        JOIN chargeback_diagram.roles r ON r.id = u.role_id
        WHERE u.cognito_sub = @CognitoSub
        """;

    private const string PermissionsSql = """
        SELECT p.name
        FROM chargeback_diagram.role_permissions rp
        JOIN chargeback_diagram.permissions p ON p.id = rp.permission_id
        WHERE rp.role_id = @RoleId AND p.is_active
        """;

    // Explicit, currently valid grants only. NULL users.bank_id never implies all banks (ADR-0003).
    private const string ScopesSql = """
        SELECT s.bank_id
        FROM chargeback_diagram.user_bank_scopes s
        WHERE s.user_id = @UserId
          AND s.valid_from <= @Now
          AND (s.valid_until IS NULL OR s.valid_until > @Now)
        """;

    public async Task<UserAccess?> FindByCognitoSubAsync(string cognitoSub, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = await db.QuerySingleOrDefaultAsync<UserRow>(UserSql, new { CognitoSub = cognitoSub }, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var userType = ParseUserType(row.UserType);
        var permissions = row.RoleIsActive
            ? (await db.QueryAsync<string>(PermissionsSql, new { row.RoleId }, cancellationToken)).ToHashSet(StringComparer.Ordinal)
            : [];

        // Bank users never consult user_bank_scopes; processor/admin users only ever get their explicit grants.
        var grants = userType == UserType.Bank ? [] : await db.QueryAsync<Guid>(ScopesSql, new { row.UserId, Now = now }, cancellationToken);
        var scopes = BankScopeResolution.For(userType, row.BankId, grants);

        return new UserAccess(
            row.UserId,
            row.CognitoSub,
            row.Email,
            row.FullName,
            userType,
            ParseUserType(row.RoleType),
            row.RoleId,
            row.RoleName,
            row.RoleIsActive,
            row.BankId,
            row.Status,
            permissions,
            scopes);
    }

    private static UserType ParseUserType(string value) => Enum.Parse<UserType>(value, ignoreCase: true);

    private sealed class UserRow
    {
        public Guid UserId { get; set; }

        public string CognitoSub { get; set; } = "";

        public string Email { get; set; } = "";

        public string FullName { get; set; } = "";

        public string UserType { get; set; } = "";

        public Guid RoleId { get; set; }

        public Guid? BankId { get; set; }

        public string Status { get; set; } = "";

        public string RoleName { get; set; } = "";

        public string RoleType { get; set; } = "";

        public bool RoleIsActive { get; set; }
    }
}
