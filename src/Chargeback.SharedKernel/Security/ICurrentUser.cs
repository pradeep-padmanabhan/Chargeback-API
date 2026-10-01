namespace Chargeback.SharedKernel.Security;

/// <summary>Mirrors <c>users.user_type</c> / <c>roles.role_type</c>.</summary>
public enum UserType
{
    Processor,
    Bank,
    Admin,
}

/// <summary>
/// The authenticated platform user for the current request, resolved from the database
/// (never from token claims other than the Cognito <c>sub</c>).
/// </summary>
public interface ICurrentUser
{
    bool IsResolved { get; }

    Guid UserId { get; }

    UserType UserType { get; }

    Guid RoleId { get; }

    /// <summary><c>users.bank_id</c>; set only for bank users.</summary>
    Guid? HomeBankId { get; }

    IReadOnlySet<string> Permissions { get; }

    /// <summary>
    /// Banks this user may access right now. Bank users: their own bank only.
    /// Processor and admin users: currently valid <c>user_bank_scopes</c> rows only.
    /// A NULL <c>bank_id</c> never grants access to every bank.
    /// </summary>
    IReadOnlySet<Guid> BankScopes { get; }

    bool HasPermission(string permission);

    bool CanAccessBank(Guid bankId);
}
