using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.Api.Features.ClientPortal.Contracts;

// Bank-facing, curated views: no internal analyst notes, no triage internals, no other bank's data.

public sealed record PortalCaseSummaryDto(Guid Id, string CaseReference, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Stage-level progress derived from the case event log.</summary>
public sealed record PortalProgressEntryDto(string Stage, DateTimeOffset OccurredAt);

public sealed record PortalDocumentSlotDto(Guid Id, string SlotName, bool IsRequired, IReadOnlyList<PortalDocumentDto> Documents);

public sealed record PortalDocumentDto(Guid Id, string FileName, DocumentStage SchemeStage, DocumentStatus DocumentStatus, DateTimeOffset UploadedAt);

public sealed record PortalCaseDetailDto(
    Guid Id,
    string CaseReference,
    string Status,
    string? CardNumberMasked,
    DateTimeOffset? TransactionDate,
    decimal? TransactionAmount,
    string? CurrencyCode,
    string? MerchantName,
    IReadOnlyList<PortalProgressEntryDto> Progress,
    IReadOnlyList<PortalDocumentSlotDto> DocumentChecklist);

/// <summary><c>portal_messages.sender_type</c></summary>
public enum MessageSenderType
{
    Bank,
    Processor,
}

public sealed record PortalMessageDto(Guid Id, Guid CaseId, MessageSenderType SenderType, Guid? SenderId, string MessageText, DateTimeOffset CreatedAt);

public sealed record PostMessageRequest(string MessageText);
