using Chargeback.SharedKernel.Events;

namespace Chargeback.Api.Features.Cases.Contracts;

/// <summary>
/// Every case status change, written before the status update in the same transaction (ADR-0109). Raised by
/// <c>/transitions</c> and by the Human Review decision.
/// </summary>
public sealed record CaseStatusChanged : DomainEvent
{
    public override string EventType => "case.status.changed";

    public required string FromStatus { get; init; }

    public required string ToStatus { get; init; }

    public required string Action { get; init; }

    /// <summary>The analyst's rationale; null when the action does not require one and none was given.</summary>
    public required string? Reason { get; init; }

    public required Guid ChangedBy { get; init; }
}
