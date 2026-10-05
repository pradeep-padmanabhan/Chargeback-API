using Chargeback.SharedKernel.Events;

namespace Chargeback.SharedKernel.Entities;

public interface IHasCreatedAt
{
    DateTimeOffset CreatedAt { get; set; }
}

public abstract class BaseEntity : IHasDomainEvents
{
    private readonly List<DomainEvent> _domainEvents = [];

    /// <summary>Application-assigned, time-ordered identifier (UUIDv7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public IReadOnlyCollection<DomainEvent> DomainEvents => _domainEvents;

    public void AddDomainEvent(DomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    public void ClearDomainEvents() => _domainEvents.Clear();
}

/// <summary>Entity with created/updated timestamps and an optimistic concurrency token.</summary>
public abstract class AuditableEntity : BaseEntity, IHasCreatedAt
{
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency token maintained by the database.</summary>
    public uint Version { get; set; }
}
