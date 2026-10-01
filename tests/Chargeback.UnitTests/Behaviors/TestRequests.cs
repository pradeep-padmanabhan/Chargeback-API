using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Security;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;

namespace Chargeback.UnitTests.Behaviors;

[RequirePermission("VIEW_CASES")]
public sealed record BankScopedTestQuery(Guid BankId) : IQuery<string>, IBankScopedRequest;

[RequirePermission("VIEW_CASES")]
public sealed record ResourceScopedTestQuery(Guid CaseId) : IQuery<string>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

[RequirePermission("VIEW_CASES")]
[RestrictToUserTypes(UserType.Bank)]
public sealed record BankUsersOnlyTestQuery : IQuery<string>, IScopeFilteredRequest;

[AllowAnyPlatformUser]
[NotBankScoped("test")]
public sealed record AnyUserTestQuery : IQuery<string>;

public sealed record UndeclaredTestQuery : IQuery<string>;

[AllowAnyPlatformUser]
[RequirePermission("VIEW_CASES")]
public sealed record DoublePermissionTestQuery : IQuery<string>, IScopeFilteredRequest;

[RequirePermission("VIEW_CASES")]
[NotBankScoped("test")]
public sealed record DoubleScopeTestQuery(Guid BankId) : IQuery<string>, IBankScopedRequest;

[AllowAnyPlatformUser]
[NotBankScoped("test")]
public sealed record TransactionalTestCommand : ICommand, ITransactionalCommand;

[AllowAnyPlatformUser]
[NotBankScoped("test")]
public sealed record PlainTestCommand : ICommand;

[AllowAnyPlatformUser]
[NotBankScoped("test")]
public sealed record ValidatedTestCommand(string? Name, int Count) : ICommand<int>;

/// <summary>Controllable current user for behavior tests.</summary>
public sealed class FakeCurrentUser : ICurrentUser, ICurrentUserLoader
{
    public Result LoadResult { get; set; } = Result.Success();

    public int LoadCalls { get; private set; }

    public bool IsResolved => LoadResult.IsSuccess;

    public Guid UserId { get; set; } = Guid.NewGuid();

    public UserType UserType { get; set; } = UserType.Processor;

    public Guid RoleId { get; set; } = Guid.NewGuid();

    public Guid? HomeBankId { get; set; }

    public HashSet<string> PermissionSet { get; } = new(StringComparer.Ordinal);

    public HashSet<Guid> Scopes { get; } = [];

    public IReadOnlySet<string> Permissions => PermissionSet;

    public IReadOnlySet<Guid> BankScopes => Scopes;

    public bool HasPermission(string permission) => PermissionSet.Contains(permission);

    public bool CanAccessBank(Guid bankId) => Scopes.Contains(bankId);

    public Task<Result> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        LoadCalls++;
        return Task.FromResult(LoadResult);
    }
}
