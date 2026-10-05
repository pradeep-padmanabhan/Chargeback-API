using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Security;

namespace Chargeback.Api.Common.Security;

// Every MediatR request declares exactly one PERMISSION rule and exactly one SCOPE rule.
// Anything else is denied (deny by default) and fails the architecture tests.

// ---- Permission rule (exactly one) ------------------------------------------------------

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class RequirePermissionAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}

/// <summary>Any active, provisioned platform user (e.g. reading one's own profile).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AllowAnyPlatformUserAttribute : Attribute;

// ---- Optional user-type restriction -----------------------------------------------------

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class RestrictToUserTypesAttribute(params UserType[] userTypes) : Attribute
{
    public IReadOnlyList<UserType> UserTypes { get; } = userTypes;
}

// ---- Scope rule (exactly one) -----------------------------------------------------------

/// <summary>The request names the bank it acts on; the caller must hold scope for it.</summary>
public interface IBankScopedRequest
{
    Guid BankId { get; }
}

/// <summary>The request targets a resource whose owning bank is resolved from the database.</summary>
public interface IResourceScopedRequest
{
    ScopedResource Resource { get; }
}

/// <summary>The handler filters results to <see cref="ICurrentUser.BankScopes"/> (list/queue queries).</summary>
public interface IScopeFilteredRequest;

/// <summary>Not bank-owned (global configuration, own profile). A justification is mandatory.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class NotBankScopedAttribute(string justification) : Attribute
{
    public string Justification { get; } = justification;
}

public readonly record struct ScopedResource(ScopedResourceKind Kind, Guid Id);

public enum ScopeRule
{
    None,
    BankScoped,
    ResourceScoped,
    ScopeFiltered,
    NotBankScoped,
}

/// <summary>Reflected, cached authorization declaration of a request type.</summary>
public sealed record RequestAuthorizationMetadata(
    Type RequestType,
    string? Permission,
    bool AllowAnyPlatformUser,
    IReadOnlyList<UserType>? AllowedUserTypes,
    ScopeRule Scope,
    IReadOnlyList<string> Problems)
{
    /// <summary>Internal workflow step; allowed only inside <see cref="SystemExecution"/> (ADR-0123).</summary>
    public bool IsSystemOperation { get; init; }

    public bool IsValid => Problems.Count == 0;

    public static RequestAuthorizationMetadata For(Type requestType)
    {
        ArgumentNullException.ThrowIfNull(requestType);

        var problems = new List<string>();
        var permission = requestType.GetCustomAttributes(typeof(RequirePermissionAttribute), false)
            .Cast<RequirePermissionAttribute>().SingleOrDefault();
        var anyUser = requestType.IsDefined(typeof(AllowAnyPlatformUserAttribute), false);
        var userTypes = requestType.GetCustomAttributes(typeof(RestrictToUserTypesAttribute), false)
            .Cast<RestrictToUserTypesAttribute>().SingleOrDefault()?.UserTypes;
        var system = requestType.GetCustomAttributes(typeof(SystemOperationAttribute), false)
            .Cast<SystemOperationAttribute>().SingleOrDefault();
        var notScoped = requestType.GetCustomAttributes(typeof(NotBankScopedAttribute), false)
            .Cast<NotBankScopedAttribute>().SingleOrDefault();

        if ((permission is not null ? 1 : 0) + (anyUser ? 1 : 0) + (system is not null ? 1 : 0) != 1)
        {
            problems.Add("must declare exactly one of [RequirePermission], [AllowAnyPlatformUser] or [SystemOperation]");
        }

        if (system is not null && (string.IsNullOrWhiteSpace(system.Justification) || userTypes is not null))
        {
            problems.Add("[SystemOperation] requires a justification and cannot be combined with [RestrictToUserTypes]");
        }

        if (permission is not null && string.IsNullOrWhiteSpace(permission.Permission))
        {
            problems.Add("[RequirePermission] must name a permission");
        }

        if (userTypes is { Count: 0 })
        {
            problems.Add("[RestrictToUserTypes] must list at least one user type");
        }

        var scopes = new List<ScopeRule>();
        if (typeof(IBankScopedRequest).IsAssignableFrom(requestType))
        {
            scopes.Add(ScopeRule.BankScoped);
        }

        if (typeof(IResourceScopedRequest).IsAssignableFrom(requestType))
        {
            scopes.Add(ScopeRule.ResourceScoped);
        }

        if (typeof(IScopeFilteredRequest).IsAssignableFrom(requestType))
        {
            scopes.Add(ScopeRule.ScopeFiltered);
        }

        if (notScoped is not null)
        {
            scopes.Add(ScopeRule.NotBankScoped);
            if (string.IsNullOrWhiteSpace(notScoped.Justification))
            {
                problems.Add("[NotBankScoped] requires a justification");
            }
        }

        if (scopes.Count != 1)
        {
            problems.Add("must declare exactly one scope rule: IBankScopedRequest, IResourceScopedRequest, IScopeFilteredRequest or [NotBankScoped]");
        }

        return new RequestAuthorizationMetadata(
            requestType,
            permission?.Permission,
            anyUser,
            userTypes,
            scopes.Count == 1 ? scopes[0] : ScopeRule.None,
            problems)
        {
            IsSystemOperation = system is not null,
        };
    }
}
