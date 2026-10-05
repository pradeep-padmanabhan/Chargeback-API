using System.Text.Json;
using System.Text.Json.Serialization;
using Chargeback.SharedKernel.Events;

namespace Chargeback.Infrastructure.Outbox;

/// <summary>
/// Versioned event contract published to SNS FIFO (docs/contracts/events.md).
/// Stored verbatim in <c>domain_events.event_data</c>. <see cref="EventId"/> equals
/// <c>domain_events.id</c> and is the consumer de-duplication key.
/// </summary>
public sealed record IntegrationEventEnvelope(
    Guid EventId,
    string EventType,
    int SchemaVersion,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    Guid? BankId,
    Guid? CaseId,
    JsonElement Data)
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static IntegrationEventEnvelope From(DomainEvent domainEvent, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // Serialize as the runtime type so derived event fields are included.
        var data = JsonSerializer.SerializeToElement(domainEvent, domainEvent.GetType(), SerializerOptions);
        return new IntegrationEventEnvelope(
            domainEvent.EventId,
            domainEvent.EventType,
            domainEvent.SchemaVersion,
            domainEvent.OccurredAt,
            correlationId,
            domainEvent.BankId,
            domainEvent.CaseId,
            data);
    }

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}
