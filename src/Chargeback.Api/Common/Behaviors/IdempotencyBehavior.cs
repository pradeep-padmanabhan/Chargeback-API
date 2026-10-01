using System.Reflection;
using Chargeback.Api.Common.Http;
using Chargeback.Api.Common.Idempotency;
using Chargeback.Api.Common.Results;
using Chargeback.Infrastructure.Persistence.Queries;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chargeback.Api.Common.Behaviors;

/// <summary>
/// Pipeline step 4 of 5 (ADR-0106, approved): Logging → Validation → Authorization → <b>Idempotency</b> → Transaction.
/// Runs after authorization so an unauthorized caller learns nothing, and before any business effect.
/// Only requests declared <see cref="IdempotentAttribute"/> are affected. Keys are scoped per principal and
/// operation; expired rows (90 days) are treated as absent whether or not the nightly purge has run.
/// </summary>
public sealed partial class IdempotencyBehavior<TRequest, TResponse>(
    IdempotencyContext context,
    IDapperQueryService db,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<IdempotencyBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private const string KeyConstraint = "idempotency_keys_principal_operation_key";

    private static readonly IdempotentAttribute? Declaration = typeof(TRequest).GetCustomAttribute<IdempotentAttribute>();

    private const string FindSql = """
        SELECT request_hash, state, response_body::text AS response_body
        FROM chargeback_diagram.idempotency_keys
        WHERE principal_id = @PrincipalId AND operation = @Operation AND idempotency_key = @Key AND expires_at > @Now
        """;

    private const string DeleteExpiredSql = """
        WITH deleted AS (
          DELETE FROM chargeback_diagram.idempotency_keys
          WHERE principal_id = @PrincipalId AND operation = @Operation AND idempotency_key = @Key AND expires_at <= @Now
          RETURNING 1)
        SELECT count(*) FROM deleted
        """;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (Declaration is null)
        {
            return await next(cancellationToken);
        }

        var key = ((IIdempotentCommand)request).IdempotencyKey;
        if (!IdempotencyKey.IsValid(key))
        {
            return ResultFailure<TResponse>.Create(Errors.IdempotencyKeyRequired);
        }

        if (Declaration.Mode == IdempotencyMode.Reservation)
        {
            return ResultFailure<TResponse>.Create(IdempotencyErrors.ReservationNotImplemented);
        }

        var hash = IdempotencyHash.Compute(Declaration.Operation, request);
        var scope = new { PrincipalId = currentUser.UserId, Declaration.Operation, Key = key, Now = timeProvider.GetUtcNow() };

        // An expired key may be reused: remove it so the new result can be stored under the same key.
        await db.ExecuteScalarAsync<long>(DeleteExpiredSql, scope, cancellationToken);

        if (await db.QuerySingleOrDefaultAsync<StoredKey>(FindSql, scope, cancellationToken) is { } existing)
        {
            return Resolve(existing, hash);
        }

        context.Begin(Declaration, currentUser.UserId, key!, hash);
        try
        {
            var response = await next(cancellationToken);
            if (response is Result { IsSuccess: true } && !context.Recorded)
            {
                throw new InvalidOperationException(
                    $"{typeof(TRequest).Name} is idempotent (atomic mode) but its handler did not call IdempotencyContext.RecordCompleted.");
            }

            return response;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: KeyConstraint })
        {
            // Lost a concurrent first-time race: the business effect rolled back with the key row. Replay the winner.
            var winner = await db.QuerySingleOrDefaultAsync<StoredKey>(FindSql, scope, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            LogRaceReplayed(logger, Declaration.Operation);
            return Resolve(winner, hash);
        }
    }

    private TResponse Resolve(StoredKey stored, string hash)
    {
        if (!string.Equals(stored.RequestHash, hash, StringComparison.Ordinal))
        {
            return ResultFailure<TResponse>.Create(IdempotencyErrors.KeyReused);
        }

        if (stored.State != "COMPLETED" || stored.ResponseBody is null)
        {
            return ResultFailure<TResponse>.Create(IdempotencyErrors.InProgress);
        }

        context.MarkReplayed();
        LogReplayed(logger, Declaration!.Operation);
        return ResultReplay<TResponse>.FromJson(stored.ResponseBody);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Idempotent replay of {Operation}")]
    private static partial void LogReplayed(ILogger logger, string operation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Concurrent duplicate of {Operation} lost the race; replaying the stored result")]
    private static partial void LogRaceReplayed(ILogger logger, string operation);

    private sealed class StoredKey
    {
        public string RequestHash { get; set; } = "";

        public string State { get; set; } = "";

        public string? ResponseBody { get; set; }
    }
}
