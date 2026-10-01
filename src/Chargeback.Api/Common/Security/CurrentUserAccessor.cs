using System.Security.Claims;
using Chargeback.Api.Common.Results;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;

namespace Chargeback.Api.Common.Security;

/// <summary>Source of the authenticated principal: HTTP today, a system principal for workers later.</summary>
public interface IPrincipalAccessor
{
    ClaimsPrincipal? Principal { get; }
}

internal sealed class HttpContextPrincipalAccessor(IHttpContextAccessor accessor) : IPrincipalAccessor
{
    public ClaimsPrincipal? Principal => accessor.HttpContext?.User;
}

public interface ICurrentUserLoader
{
    /// <summary>Resolves the platform user once per scope. Failures are 401/403 results.</summary>
    Task<Result> EnsureLoadedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Scoped <see cref="ICurrentUser"/> populated from the database using only the Cognito <c>sub</c>.
/// User type, role, permissions and bank scopes all come from the database (ADR-0003).
/// </summary>
public sealed class CurrentUserAccessor(IPrincipalAccessor principalAccessor, IUserAccessStore store, TimeProvider timeProvider)
    : ICurrentUser, ICurrentUserLoader
{
    public const string SubjectClaim = "sub";

    private UserAccess? _access;
    private Result? _loadResult;

    public bool IsResolved => _access is not null;

    public UserAccess Access => _access ?? throw new InvalidOperationException(
        "Current user not resolved. It is loaded by the AuthorizationBehavior before handlers run.");

    public Guid UserId => Access.UserId;

    public UserType UserType => Access.UserType;

    public Guid RoleId => Access.RoleId;

    public Guid? HomeBankId => Access.HomeBankId;

    public IReadOnlySet<string> Permissions => Access.Permissions;

    public IReadOnlySet<Guid> BankScopes => Access.BankScopes;

    public bool HasPermission(string permission) => Access.Permissions.Contains(permission);

    public bool CanAccessBank(Guid bankId) => Access.BankScopes.Contains(bankId);

    public async Task<Result> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loadResult is not null)
        {
            return _loadResult;
        }

        var principal = principalAccessor.Principal;
        var subject = principal?.Identity?.IsAuthenticated == true ? principal.FindFirstValue(SubjectClaim) : null;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return _loadResult = Result.Failure(Errors.Unauthenticated);
        }

        var access = await store.FindByCognitoSubAsync(subject, timeProvider.GetUtcNow(), cancellationToken);
        _loadResult = access switch
        {
            null => Result.Failure(Errors.UserNotProvisioned),
            { Status: not "ACTIVE" } => Result.Failure(Errors.UserNotActive),
            _ when access.UserType != access.RoleType => Result.Failure(Errors.UserRoleMismatch),
            _ => Result.Success(),
        };

        if (_loadResult.IsSuccess)
        {
            _access = access;
        }

        return _loadResult;
    }
}
