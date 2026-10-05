using Chargeback.SharedKernel.Entities;

namespace Chargeback.Infrastructure.Persistence.Entities;

/// <summary><c>portal_messages</c></summary>
public sealed class PortalMessage : BaseEntity, IHasCreatedAt
{
    public Guid CaseId { get; set; }

    /// <summary>BANK | PROCESSOR</summary>
    public required string SenderType { get; set; }

    public Guid? SenderId { get; set; }

    public required string MessageText { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary><c>zendesk_tickets</c></summary>
public sealed class ZendeskTicket : AuditableEntity
{
    public Guid CaseId { get; set; }

    /// <summary>External Zendesk ticket identifier (column <c>zendesk_ticket_id</c>, varchar).</summary>
    public required string ZendeskTicketId { get; set; }

    public string? Status { get; set; }
}

/// <summary><c>zendesk_events</c></summary>
public sealed class ZendeskEvent : BaseEntity, IHasCreatedAt
{
    /// <summary>FK to zendesk_tickets.id (column <c>zendesk_ticket_id</c>, uuid).</summary>
    public Guid ZendeskTicketId { get; set; }

    public string? EventType { get; set; }

    /// <summary>jsonb</summary>
    public string EventData { get; set; } = "{}";

    public bool HmacVerified { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary><c>mastercom_filings</c></summary>
public sealed class MastercomFiling : AuditableEntity
{
    public Guid CaseId { get; set; }

    public string? MastercomReference { get; set; }

    public string? FilingStatus { get; set; }

    public string? CurrentStage { get; set; }

    public required string IdempotencyKey { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }
}

/// <summary><c>filing_api_log</c></summary>
public sealed class FilingApiLogEntry : BaseEntity, IHasCreatedAt
{
    public Guid? FilingId { get; set; }

    /// <summary>OUTBOUND | INBOUND</summary>
    public required string Direction { get; set; }

    public string? Endpoint { get; set; }

    /// <summary>jsonb</summary>
    public string? RequestPayload { get; set; }

    /// <summary>jsonb</summary>
    public string? ResponsePayload { get; set; }

    public int? HttpStatus { get; set; }

    public int? LatencyMs { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary><c>ai_decision_logs</c></summary>
public sealed class AiDecisionLog : BaseEntity, IHasCreatedAt
{
    public Guid? CaseId { get; set; }

    /// <summary>Migration 0011: the invocation's bank (row-level security).</summary>
    public Guid? BankId { get; set; }

    public required string AgentName { get; set; }

    public required string CapabilityName { get; set; }

    public string? ModelName { get; set; }

    public string? PromptTemplateId { get; set; }

    public string? InputHash { get; set; }

    /// <summary>jsonb</summary>
    public string? RawResponse { get; set; }

    /// <summary>jsonb</summary>
    public string? ParsedOutput { get; set; }

    public int? LatencyMs { get; set; }

    public int? TokenCountInput { get; set; }

    public int? TokenCountOutput { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary><c>domain_events</c>: the transactional outbox.</summary>
public sealed class DomainEventRecord : BaseEntity, IHasCreatedAt
{
    public Guid? CaseId { get; set; }

    /// <summary>Migration 0011: the event's bank (row-level security). Null only for events with no bank.</summary>
    public Guid? BankId { get; set; }

    public required string EventType { get; set; }

    /// <summary>jsonb: the full <see cref="Outbox.IntegrationEventEnvelope"/>.</summary>
    public required string EventData { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary><c>processed_domain_events</c> (migration 0003): one row per consumed outbox event, for idempotent consumers.</summary>
public sealed class ProcessedDomainEvent
{
    public Guid EventId { get; set; }

    public required string Consumer { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary><c>idempotency_keys</c> (migration 0004, ADR-0106): stored result of an idempotent API operation.</summary>
public sealed class IdempotencyKeyRecord
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid PrincipalId { get; set; }

    public required string Operation { get; set; }

    public required string IdempotencyKey { get; set; }

    public required string RequestHash { get; set; }

    /// <summary>IN_PROGRESS | COMPLETED</summary>
    public required string State { get; set; }

    public int? ResponseStatus { get; set; }

    /// <summary>jsonb</summary>
    public string? ResponseBody { get; set; }

    public Guid? ResourceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
