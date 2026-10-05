using Chargeback.Api.Common.Messaging;
using Chargeback.Infrastructure.Persistence;
using Chargeback.SharedKernel.Results;
using MediatR;

namespace Chargeback.Api.Common.Behaviors;

/// <summary>
/// Pipeline step 4. Only for requests marked <see cref="ITransactionalCommand"/>: one transaction,
/// SaveChanges (which also writes outbox rows) and commit on success; rollback on failure result or
/// exception. While the transaction is open, external adapters refuse to run (<see cref="ExternalCallGuard"/>).
/// </summary>
public sealed class TransactionBehavior<TRequest, TResponse>(IUnitOfWork unitOfWork)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly bool IsTransactional = typeof(ITransactionalCommand).IsAssignableFrom(typeof(TRequest));

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (!IsTransactional || unitOfWork.HasActiveTransaction)
        {
            return await next(cancellationToken);
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        using var guard = ExternalCallGuard.EnterDatabaseTransaction();
        try
        {
            var response = await next(cancellationToken);
            if (response is Result { IsFailure: true })
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return response;
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return response;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
