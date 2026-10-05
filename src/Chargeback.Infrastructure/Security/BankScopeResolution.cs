using Chargeback.SharedKernel.Security;

namespace Chargeback.Infrastructure.Security;

/// <summary>
/// The banks a user may access (ADR-0003). A bank user: its own <c>users.bank_id</c> only (the database requires it for
/// BANK users; if it were NULL the user would get no bank, never all banks). A processor or admin user: its explicit,
/// currently valid <c>user_bank_scopes</c> grants only; its NULL <c>bank_id</c> never implies cross-bank access.
/// </summary>
public static class BankScopeResolution
{
    public static IReadOnlySet<Guid> For(UserType userType, Guid? homeBankId, IEnumerable<Guid> validGrants)
    {
        ArgumentNullException.ThrowIfNull(validGrants);
        return userType == UserType.Bank
            ? homeBankId is { } own ? new HashSet<Guid> { own } : new HashSet<Guid>()
            : validGrants.ToHashSet();
    }
}
