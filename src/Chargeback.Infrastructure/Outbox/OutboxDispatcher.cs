using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Chargeback.Infrastructure.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Off by default: no SNS publisher is wired until the messaging contract is approved.</summary>
    public bool DispatcherEnabled { get; set; }

    public int BatchSize { get; set; } = 50;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>"Logging" (default) or "InProcess" (see <see cref="OutboxTransports"/>).</summary>
    public string Transport { get; set; } = OutboxTransports.Logging;
}

/// <summary>Publishes an outbox envelope to the message bus (SNS FIFO in production).</summary>
public interface IIntegrationEventPublisher
{
    /// <param name="messageGroupId">FIFO ordering key (case id when present).</param>
    /// <param name="deduplicationId">Equals the event id; consumers must also de-duplicate on it.</param>
    Task PublishAsync(string eventType, string envelopeJson, string messageGroupId, string deduplicationId, CancellationToken cancellationToken);
}

/// <summary>Placeholder publisher: logs event metadata only (never payloads).</summary>
internal sealed partial class LoggingIntegrationEventPublisher(ILogger<LoggingIntegrationEventPublisher> logger) : IIntegrationEventPublisher
{
    public Task PublishAsync(string eventType, string envelopeJson, string messageGroupId, string deduplicationId, CancellationToken cancellationToken)
    {
        LogPublished(logger, eventType, deduplicationId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox event {EventType} {EventId} published (logging publisher)")]
    private static partial void LogPublished(ILogger logger, string eventType, string eventId);
}

/// <summary>
/// Claims unpublished <c>domain_events</c> rows with <c>FOR UPDATE SKIP LOCKED</c> (safe with several
/// ECS tasks), publishes them and stamps <c>published_at</c>. Delivery is at-least-once.
/// </summary>
public sealed partial class OutboxDispatcher(
    NpgsqlDataSource dataSource,
    IIntegrationEventPublisher publisher,
    TimeProvider timeProvider,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private const string ClaimSql = """
        SELECT id, case_id, event_type, event_data::text AS event_data
        FROM chargeback_diagram.domain_events
        WHERE published_at IS NULL
        ORDER BY created_at, id
        LIMIT @BatchSize
        FOR UPDATE SKIP LOCKED
        """;

    private const string MarkSql = """
        UPDATE chargeback_diagram.domain_events SET published_at = @PublishedAt WHERE id = @Id
        """;

    /// <summary>Processes one batch. Returns the number of events published.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var pending = (await connection.QueryAsync<PendingEvent>(
            new CommandDefinition(ClaimSql, new { options.Value.BatchSize }, transaction, cancellationToken: cancellationToken))).AsList();

        foreach (var evt in pending)
        {
            await publisher.PublishAsync(
                evt.EventType,
                evt.EventData,
                (evt.CaseId ?? evt.Id).ToString(),
                evt.Id.ToString(),
                cancellationToken);

            await connection.ExecuteAsync(new CommandDefinition(
                MarkSql, new { evt.Id, PublishedAt = timeProvider.GetUtcNow() }, transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return pending.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.DispatcherEnabled)
        {
            LogDisabled(logger);
            return;
        }

        using var timer = new PeriodicTimer(options.Value.PollInterval, timeProvider);
        do
        {
            try
            {
                var count = await DispatchBatchAsync(stoppingToken);
                if (count > 0)
                {
                    LogDispatched(logger, count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDispatchFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox dispatcher disabled by configuration")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox dispatched {Count} events")]
    private static partial void LogDispatched(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox dispatch batch failed; will retry")]
    private static partial void LogDispatchFailed(ILogger logger, Exception exception);

    private sealed class PendingEvent
    {
        public Guid Id { get; set; }

        public Guid? CaseId { get; set; }

        public string EventType { get; set; } = "";

        public string EventData { get; set; } = "";
    }
}
