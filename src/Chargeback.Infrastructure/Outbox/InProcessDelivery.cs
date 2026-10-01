using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chargeback.Infrastructure.Outbox;

/// <summary>
/// A consumer of outbox events. Consumers must be idempotent (at-least-once delivery): record the consumed
/// <c>eventId</c> in <c>processed_domain_events</c> atomically with their effect and skip repeats.
/// Throwing signals a retryable failure: the event stays unpublished and is delivered again.
/// </summary>
public interface IIntegrationEventConsumer
{
    string Name { get; }

    bool Handles(string eventType);

    Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>Outbox transport selection (<c>Outbox:Transport</c>).</summary>
public static class OutboxTransports
{
    /// <summary>Default: logs event metadata only.</summary>
    public const string Logging = "Logging";

    /// <summary>
    /// Delivers each event to in-process consumers (Phase 7 development transport). SNS/SQS replaces it once the
    /// messaging contract is approved; consumers are unchanged.
    /// </summary>
    public const string InProcess = "InProcess";
}

/// <summary>Delivers outbox events to <see cref="IIntegrationEventConsumer"/>s in a fresh DI scope per event.</summary>
internal sealed partial class InProcessIntegrationEventPublisher(IServiceScopeFactory scopes, ILogger<InProcessIntegrationEventPublisher> logger)
    : IIntegrationEventPublisher
{
    public async Task PublishAsync(string eventType, string envelopeJson, string messageGroupId, string deduplicationId, CancellationToken cancellationToken)
    {
        var envelope = JsonSerializer.Deserialize<IntegrationEventEnvelope>(envelopeJson, IntegrationEventEnvelope.SerializerOptions)
            ?? throw new InvalidOperationException($"Outbox event {deduplicationId} has no envelope.");

        await using var scope = scopes.CreateAsyncScope();
        foreach (var consumer in scope.ServiceProvider.GetServices<IIntegrationEventConsumer>().Where(c => c.Handles(eventType)))
        {
            LogDelivering(logger, eventType, envelope.EventId, consumer.Name);
            await consumer.HandleAsync(envelope, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Delivering {EventType} {EventId} to {Consumer}")]
    private static partial void LogDelivering(ILogger logger, string eventType, Guid eventId, string consumer);
}
