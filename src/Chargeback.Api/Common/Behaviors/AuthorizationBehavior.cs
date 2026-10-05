using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using MediatR;

namespace Chargeback.Api.Common.Behaviors;

/// <summary>
/// Pipeline step 3. Deny by default. Order of checks:
/// declaration valid → user resolved (401/403) → user type (403) → permission (403) →
/// bank scope (404, indistinguishable from "does not exist").
/// </summary>
public sealed partial class AuthorizationBehavior<TRequest, TResponse>(
    ICurrentUserLoader loader,
    ICurrentUser currentUser,
    IResourceBankResolver resourceResolver,
    ILogger<AuthorizationBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly RequestAuthorizationMetadata Metadata = RequestAuthorizationMetadata.For(typeof(TRequest));

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var decision = await AuthorizeAsync(request, cancellationToken);
        return decision is null ? await next(cancellationToken) : ResultFailure<TResponse>.Create(decision);
    }

    private async Task<Error?> AuthorizeAsync(TRequest request, CancellationToken cancellationToken)
    {
        if (!Metadata.IsValid)
        {
            LogMisconfigured(logger, typeof(TRequest).Name, string.Join("; ", Metadata.Problems));
            return Errors.AuthorizationMisconfigured;
        }

        // Internal workflow steps: never reachable by users; the workflow itself is cross-bank by design.
        if (Metadata.IsSystemOperation)
        {
            return SystemExecution.IsActive ? null : Errors.SystemOperationOnly;
        }

        var loaded = await loader.EnsureLoadedAsync(cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        if (Metadata.AllowedUserTypes is { } allowed && !allowed.Contains(currentUser.UserType))
        {
            return Errors.UserTypeNotAllowed;
        }

        if (Metadata.Permission is { } permission && !currentUser.HasPermission(permission))
        {
            return Errors.PermissionDenied;
        }

        switch (Metadata.Scope)
        {
            case ScopeRule.BankScoped:
                return currentUser.CanAccessBank(((IBankScopedRequest)request).BankId) ? null : Errors.ResourceNotFound;

            case ScopeRule.ResourceScoped:
                var resource = ((IResourceScopedRequest)request).Resource;
                var owningBank = await resourceResolver.ResolveBankIdAsync(resource.Kind, resource.Id, cancellationToken);
                return owningBank is { } bankId && currentUser.CanAccessBank(bankId) ? null : Errors.ResourceNotFound;

            case ScopeRule.ScopeFiltered:
            case ScopeRule.NotBankScoped:
                return null;

            default:
                return Errors.AuthorizationMisconfigured;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Request {RequestName} has an invalid authorization declaration: {Problems}")]
    private static partial void LogMisconfigured(ILogger logger, string requestName, string problems);
}
