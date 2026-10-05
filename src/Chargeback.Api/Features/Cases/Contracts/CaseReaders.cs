namespace Chargeback.Api.Features.Cases.Contracts;

/// <summary>Reads one case detail (also used by commands, inside their transaction, to return the updated case).</summary>
public interface ICaseDetailReader
{
    Task<CaseDetailDto?> ReadAsync(Guid caseId, CancellationToken cancellationToken);
}

/// <summary>The append-only case timeline: <c>domain_events</c> for the case, oldest first (ADR-0109).</summary>
public interface ICaseTimelineReader
{
    Task<IReadOnlyList<CaseTimelineEntryDto>> ReadAsync(Guid caseId, CancellationToken cancellationToken);
}
