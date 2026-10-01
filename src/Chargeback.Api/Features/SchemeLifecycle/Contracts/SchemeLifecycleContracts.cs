namespace Chargeback.Api.Features.SchemeLifecycle.Contracts;

/// <summary>
/// One recorded scheme lifecycle transition. Stage names come from Mastercom polling and are not
/// enumerated until the Mastercom contract is approved (Q12).
/// </summary>
public sealed record SchemeStageDto(string Stage, DateTimeOffset OccurredAt, Guid? FilingId, string? Source);
