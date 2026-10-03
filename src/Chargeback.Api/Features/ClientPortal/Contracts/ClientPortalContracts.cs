using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.Api.Features.ClientPortal.Contracts;

// Bank-facing, curated views: no internal notes, no triage or AI fields, no review rationale, no assignee, no S3 keys,
// no other bank's data. The status is the raw case status until a bank-facing vocabulary is approved (ADR-0110).

/// <summary>A case in the bank's portal list.</summary>
public sealed record PortalCaseSummaryDto(
    Guid CaseId,
    string ReferenceNumber,
    string Status,
    decimal? Amount,
    string? Currency,
    string? MerchantName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A confirmed upload: name and processing status only.</summary>
public sealed record PortalDocumentDto(string Name, DocumentStatus ProcessingStatus);

/// <summary>
/// One case for the bank. <c>ReasonCode</c> comes from deterministic rules only; <c>NetworkDeadline</c> is the
/// server-calculated filing deadline (null until derived).
/// </summary>
public sealed record PortalCaseDetailDto(
    Guid CaseId,
    string ReferenceNumber,
    string Status,
    decimal? Amount,
    string? Currency,
    string? MerchantName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ReasonCode,
    string? ReasonDescription,
    DateOnly? NetworkDeadline,
    IReadOnlyList<PortalDocumentDto> Documents);

/// <summary>
/// Who wrote a message. Stored in <c>portal_messages.sender_type</c> as BANK (bank user) or PROCESSOR (analyst; also
/// used for admin users).
/// </summary>
public static class MessageSenderTypes
{
    public const string BankUser = "BANK_USER";
    public const string Analyst = "ANALYST";
}

/// <summary>A message as the bank sees it (no sender identity).</summary>
public sealed record PortalMessageDto(Guid MessageId, string SenderType, string Body, DateTimeOffset CreatedAt);

/// <summary>A message as analysts see it.</summary>
public sealed record CaseMessageDto(Guid MessageId, Guid CaseId, string SenderType, Guid? SenderId, string Body, DateTimeOffset CreatedAt);

/// <summary>1-2000 characters; card numbers are rejected.</summary>
public sealed record PostMessageRequest(string? Body);

public sealed record SupportTicketRequest(string? Subject, string? Body);

/// <summary><c>TicketId</c> is <c>STUB-&lt;uuid&gt;</c> while Zendesk is a KNOWN_LIMITATION_ZENDESK_ stub.</summary>
public sealed record SupportTicketAcceptedDto(string TicketId);
