using Chargeback.Api.Common.Security;
using Chargeback.Infrastructure.Persistence;
using Chargeback.SharedKernel.Security;
using MediatR;

namespace Chargeback.Api.Common.Behaviors;

/// <summary>
/// Pipeline step 4 (ADR-0006): after authorization has resolved the caller, establishes the database scope that the
/// row-level security policies (migration 0010) enforce on every connection the request opens:
/// <list type="bullet">
/// <item>bank-scoped, resource-scoped and scope-filtered requests: the caller's banks
/// (<see cref="ICurrentUser.BankScopes"/>; a NULL <c>bank_id</c> never widens it);</item>
/// <item>[SystemOperation] workflow steps: system scope;</item>
/// <item>[NotBankScoped] requests: no scope; they touch no protected table, and anything they did touch would read as
/// empty (fail closed).</item>
/// </list>
/// Works for queries and non-transactional commands too, because the scope is applied as each connection opens rather
/// than inside <see cref="TransactionBehavior{TRequest,TResponse}"/>.
/// </summary>
public sealed class RlsSetupBehavior<TRequest, TResponse>(IDatabaseScope scope, ICurrentUser currentUser)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly RequestAuthorizationMetadata Metadata = RequestAuthorizationMetadata.For(typeof(TRequest));

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        switch (ScopeFor(Metadata))
        {
            case DatabaseScopeMode.System:
                await scope.UseSystemAsync(cancellationToken);
                break;
            case DatabaseScopeMode.Banks:
                await scope.UseBanksAsync(currentUser.BankScopes, cancellationToken);
                break;
        }

        return await next(cancellationToken);
    }

    /// <summary>The scope a request type gets; <see cref="DatabaseScopeMode.None"/> means the scope is left unset.</summary>
    public static DatabaseScopeMode ScopeFor(RequestAuthorizationMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.IsSystemOperation)
        {
            return DatabaseScopeMode.System;
        }

        return metadata.Scope is ScopeRule.BankScoped or ScopeRule.ResourceScoped or ScopeRule.ScopeFiltered
            ? DatabaseScopeMode.Banks
            : DatabaseScopeMode.None;
    }
}
