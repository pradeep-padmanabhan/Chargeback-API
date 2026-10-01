using System.Text.Json;

namespace Chargeback.Api.Features.Filing.Contracts;

/// <summary>
/// Prepared, not submitted. The analyst reviews <c>Payload</c> and confirms with its
/// <c>PayloadSha256</c>, proving they confirmed exactly what will be sent (storage: ADR-0102).
/// </summary>
public sealed record FilingDraftDto(
    Guid FilingId,
    Guid CaseId,
    string? FilingStatus,
    string? SchemeFunctionCode,
    JsonElement Payload,
    string PayloadSha256,
    DateOnly? FilingDeadlineDate,
    int? DaysRemaining);

/// <summary>Explicit human confirmation. Neither AI nor a background worker can issue this.</summary>
public sealed record ConfirmFilingRequest(string PayloadSha256, bool Confirmed);

public sealed record FilingDto(
    Guid Id,
    Guid CaseId,
    string? MastercomReference,
    string? FilingStatus,
    string? CurrentStage,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary><c>filing_api_log</c> row. Payloads are masked before storage.</summary>
public sealed record FilingApiLogEntryDto(
    Guid Id,
    string Direction,
    string? Endpoint,
    int? HttpStatus,
    int? LatencyMs,
    JsonElement? RequestPayload,
    JsonElement? ResponsePayload,
    DateTimeOffset CreatedAt);
