using System.Globalization;
using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Chargeback.Infrastructure.Maintenance;

public enum PurgeRunOutcome
{
    Completed,

    /// <summary>Another instance holds the advisory lock; this instance did nothing.</summary>
    SkippedLockHeld,
}

public sealed record PurgeRunResult(PurgeRunOutcome Outcome, long IdempotencyKeysDeleted, long ProcessedEventsDeleted, TimeSpan Duration);

/// <summary>
/// ADR-0106 nightly purge (approved): deletes expired <c>idempotency_keys</c> (<c>expires_at &lt;= now</c>) and
/// <c>processed_domain_events</c> older than 90 days, in batches of 5,000 rows, each batch its own short
/// transaction. A session-level advisory lock ensures only one ECS task purges; others skip that night.
/// Correctness never depends on the purge: lookups already ignore expired keys.
/// </summary>
public sealed partial class IdempotencyKeyPurgeJob(
    NpgsqlDataSource dataSource,
    IOptions<IdempotencyOptions> options,
    TimeProvider timeProvider,
    ILogger<IdempotencyKeyPurgeJob> logger) : BackgroundService
{
    /// <summary>Stable advisory-lock key for this job.</summary>
    public const long AdvisoryLockKey = 0x43_42_49_44_50_55_52_47; // "CBIDPURG"

    private const string DeleteKeysSql = """
        WITH batch AS (
          SELECT id FROM chargeback_diagram.idempotency_keys WHERE expires_at <= @Now ORDER BY expires_at LIMIT @Batch)
        DELETE FROM chargeback_diagram.idempotency_keys k USING batch WHERE k.id = batch.id
        """;

    private const string DeleteProcessedSql = """
        WITH batch AS (
          SELECT event_id FROM chargeback_diagram.processed_domain_events WHERE processed_at <= @Cutoff ORDER BY processed_at LIMIT @Batch)
        DELETE FROM chargeback_diagram.processed_domain_events p USING batch WHERE p.event_id = batch.event_id
        """;

    public async Task<PurgeRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var started = timeProvider.GetTimestamp();

        // Transaction-scoped advisory lock on a dedicated connection, held open for the whole run: it is released
        // when the transaction ends (commit, rollback or failure), so it can never be stranded on a pooled session.
        // The lock transaction touches no rows; each purge batch runs in its own short transaction.
        await using var lockConnection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken);
        var acquired = await lockConnection.ExecuteScalarAsync<bool>(
            new CommandDefinition("SELECT pg_try_advisory_xact_lock(@Key)", new { Key = AdvisoryLockKey }, lockTransaction, cancellationToken: cancellationToken));
        if (!acquired)
        {
            LogSkipped(logger);
            return new PurgeRunResult(PurgeRunOutcome.SkippedLockHeld, 0, 0, timeProvider.GetElapsedTime(started));
        }

        {
            var now = timeProvider.GetUtcNow();
            var keys = await DeleteInBatchesAsync(DeleteKeysSql, new { Now = now, Batch = settings.PurgeBatchSize }, settings.PurgeBatchSize, cancellationToken);
            var processed = await DeleteInBatchesAsync(
                DeleteProcessedSql, new { Cutoff = now - settings.ProcessedEventRetention, Batch = settings.PurgeBatchSize }, settings.PurgeBatchSize, cancellationToken);

            var result = new PurgeRunResult(PurgeRunOutcome.Completed, keys, processed, timeProvider.GetElapsedTime(started));
            LogCompleted(logger, keys, processed, result.Duration.TotalMilliseconds);
            await lockTransaction.CommitAsync(CancellationToken.None);
            return result;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.PurgeEnabled)
        {
            LogDisabled(logger);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(DelayUntilNextRun(timeProvider.GetUtcNow(), options.Value.PurgeTimeUtc), timeProvider, stoppingToken);
            try
            {
                LogStarting(logger);
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Retried at the next nightly run; the health check degrades if rows stay overdue > 48 h.
                LogFailed(logger, ex);
            }
        }
    }

    /// <summary>Time from <paramref name="now"/> until the next <c>HH:mm</c> UTC (tomorrow if already past).</summary>
    public static TimeSpan DelayUntilNextRun(DateTimeOffset now, string timeUtc)
    {
        var at = TimeOnly.ParseExact(timeUtc, "HH:mm", CultureInfo.InvariantCulture);
        var today = new DateTimeOffset(DateOnly.FromDateTime(now.UtcDateTime).ToDateTime(at), TimeSpan.Zero);
        var next = today > now ? today : today.AddDays(1);
        return next - now;
    }

    private async Task<long> DeleteInBatchesAsync(string sql, object parameters, int batchSize, CancellationToken cancellationToken)
    {
        long total = 0;
        int deleted;
        do
        {
            // Each statement autocommits: one short transaction per batch.
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            deleted = await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
            total += deleted;
        }
        while (deleted == batchSize && !cancellationToken.IsCancellationRequested);

        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Idempotency purge starting")]
    private static partial void LogStarting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Idempotency purge completed: {KeysDeleted} keys, {ProcessedDeleted} processed events deleted in {DurationMs:0} ms")]
    private static partial void LogCompleted(ILogger logger, long keysDeleted, long processedDeleted, double durationMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Idempotency purge skipped: another instance holds the advisory lock")]
    private static partial void LogSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Idempotency purge disabled by configuration")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Idempotency purge failed; will retry at the next nightly run")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

/// <summary>
/// Degraded when purgeable rows have been overdue for longer than 48 hours — i.e. no successful purge has
/// covered them — measured from the data itself, so it is correct across multiple ECS instances.
/// </summary>
internal sealed class IdempotencyPurgeHealthCheck(NpgsqlDataSource dataSource, IOptions<IdempotencyOptions> options, TimeProvider timeProvider) : IHealthCheck
{
    private const string Sql = """
        SELECT EXISTS (SELECT 1 FROM chargeback_diagram.idempotency_keys WHERE expires_at <= @KeysLagCutoff)
            OR EXISTS (SELECT 1 FROM chargeback_diagram.processed_domain_events WHERE processed_at <= @ProcessedLagCutoff)
        """;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var overdue = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                Sql,
                new { KeysLagCutoff = now - settings.HealthLagThreshold, ProcessedLagCutoff = now - settings.ProcessedEventRetention - settings.HealthLagThreshold },
                cancellationToken: cancellationToken));
            return overdue
                ? HealthCheckResult.Degraded($"Idempotency purge has not succeeded for more than {settings.HealthLagThreshold.TotalHours:0} hours.")
                : HealthCheckResult.Healthy();
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Degraded("Idempotency purge status could not be read.", ex);
        }
    }
}
