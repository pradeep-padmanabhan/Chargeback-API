using Chargeback.Infrastructure.Correlation;
using Chargeback.Infrastructure.Outbox;
using Chargeback.Infrastructure.Persistence.Entities;
using Chargeback.SharedKernel.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Chargeback.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Converts domain events raised on tracked entities into <c>domain_events</c> rows inside the
/// same SaveChanges, so state and events commit atomically (transactional outbox).
/// </summary>
internal sealed class OutboxInterceptor(ICorrelationIdProvider correlation, TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        WriteOutbox(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        WriteOutbox(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void WriteOutbox(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var sources = context.ChangeTracker.Entries<IHasDomainEvents>()
            .Select(e => e.Entity)
            .Where(e => e.DomainEvents.Count > 0)
            .ToList();

        // created_at orders the case timeline (ADR-0109), so events of one save get strictly increasing,
        // microsecond-distinct stamps in the order they were raised (UUIDv7 ids are not monotonic within a ms).
        var events = sources.SelectMany(s => s.DomainEvents).OrderBy(e => e.OccurredAt).ToList();
        var last = DateTimeOffset.MinValue;
        var floor = timeProvider.GetUtcNow();
        foreach (var domainEvent in events)
        {
            var stamp = TruncateToMicroseconds(domainEvent.OccurredAt < floor ? floor : domainEvent.OccurredAt);
            if (stamp <= last)
            {
                stamp = last.AddTicks(TimeSpan.TicksPerMicrosecond);
            }

            last = stamp;
            var envelope = IntegrationEventEnvelope.From(domainEvent, correlation.CorrelationId);
            context.Add(new DomainEventRecord
            {
                Id = domainEvent.EventId,
                CaseId = domainEvent.CaseId,
                EventType = domainEvent.EventType,
                EventData = envelope.ToJson(),
                CreatedAt = stamp,
            });
        }

        foreach (var source in sources)
        {
            source.ClearDomainEvents();
        }
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new DateTimeOffset(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMicrosecond), TimeSpan.Zero);
}
