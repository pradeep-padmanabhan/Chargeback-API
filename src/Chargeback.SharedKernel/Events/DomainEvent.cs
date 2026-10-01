namespace Chargeback.SharedKernel.Events;

/// <summary>
/// A fact that happened inside a slice. Raised on an entity and written to the
/// <c>domain_events</c> outbox in the same database transaction as the state change.
/// Event data must contain masked card data only.
/// </summary>
public abstract record DomainEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Stable dotted name, e.g. <c>case.created</c>. Part of the event contract.</summary>
    public abstract string EventType { get; }

    public virtual int SchemaVersion => 1;

    public Guid? CaseId { get; init; }

    public Guid? BankId { get; init; }
}

public interface IHasDomainEvents
{
    IReadOnlyCollection<DomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
