using Chargeback.Api.Common.Results;
using Chargeback.SharedKernel.Results;
using MediatR;

namespace Chargeback.Api.Common.Messaging;

/// <summary>Read-only request. Never wrapped in a transaction.</summary>
public interface IQuery<TResponse> : IRequest<Result<TResponse>>;

/// <summary>State-changing request returning no value.</summary>
public interface ICommand : IRequest<Result>;

/// <summary>State-changing request returning a value.</summary>
public interface ICommand<TResponse> : IRequest<Result<TResponse>>;

/// <summary>
/// Opt-in marker: the TransactionBehavior wraps this command in one database transaction and
/// saves/commits only when the handler returns success. Commands that call external systems
/// must not use it — they raise domain events and let the outbox do the external work.
/// </summary>
public interface ITransactionalCommand;

/// <summary>
/// Handler for API contract stubs (Phase 4). The full pipeline — logging, validation,
/// authorization, bank scope — still runs, so stubs return 401/403/404 exactly as the real
/// endpoint will, and 501 only once the caller is authorized.
/// </summary>
public abstract class NotImplementedHandler<TRequest, TResponse> : IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ResultFailure<TResponse>.Create(Errors.NotImplemented(typeof(TRequest).Name)));
}
