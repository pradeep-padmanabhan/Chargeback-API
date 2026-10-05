using System.Text.Json;
using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.Api.Features.Documents.Contracts;

/// <summary>
/// Upload lifecycle (<c>documents.upload_status</c>): PENDING_UPLOAD (declared; URL issued) → UPLOADED (client confirmed,
/// object present). Independent of the processing status (<see cref="DocumentStatus"/>: Pending → Processing →
/// Success | Failed). A soft-deleted document has <c>deletedAt</c>.
/// </summary>
public static class DocumentUploadStatuses
{
    public const string PendingUpload = "PENDING_UPLOAD";
    public const string Uploaded = "UPLOADED";
}

/// <summary>How far a slot is satisfied, from its live (not deleted) documents. Upload only: AI classification is advisory.</summary>
public enum SlotFulfillment
{
    /// <summary>No live document.</summary>
    Missing,

    /// <summary>Only declared uploads that are not confirmed yet.</summary>
    AwaitingUpload,

    /// <summary>At least one confirmed upload.</summary>
    Uploaded,
}

/// <summary>Scheme stage, upload status and processing status are independent fields (common guide §6 Act 4).</summary>
public sealed record DocumentSummaryDto(
    Guid Id,
    string FileName,
    string? MimeType,
    long? FileSizeBytes,
    DocumentStage SchemeStage,
    string UploadStatus,
    DocumentStatus ProcessingStatus,
    string? ProcessingFailureReason,
    DateTimeOffset UploadedAt,
    DateTimeOffset? ProcessedAt);

/// <summary>Reads a case's document checklist: slots in name order, each with its live documents (newest first).</summary>
public interface IDocumentChecklistReader
{
    Task<IReadOnlyList<DocumentSlotDto>> ReadForCaseAsync(Guid caseId, CancellationToken cancellationToken);
}

/// <summary>
/// Case checklist slot. Which slots a case needs per scheme / reason code is an open SME question (guide §8 #15): no
/// checklist is invented at case creation; slots are seeded from configuration once approved.
/// </summary>
public sealed record DocumentSlotDto(
    Guid Id,
    string SlotName,
    bool IsRequired,
    string? ExpectedType,
    SlotFulfillment Fulfillment,
    IReadOnlyList<DocumentSummaryDto> Documents);

/// <summary>One classification run (<c>document_classifications</c>). Always advisory; a human may override.</summary>
public sealed record DocumentClassificationDto(
    Guid Id,
    string Status,
    string? Category,
    decimal? Confidence,
    IReadOnlyList<string> Concerns,
    JsonElement ExtractedFields,
    string? FailureReason,
    string? ModelName,
    DateTimeOffset CreatedAt,
    bool Advisory);

/// <summary>
/// A document. <c>UploadedAt</c> is when the upload was declared; it and the other upload-stage fields (file name, MIME
/// type, size, stage, uploader) never change (ADR-0104). <c>Classification</c> is the latest run, if any.
/// </summary>
public sealed record DocumentDto(
    Guid Id,
    Guid CaseId,
    Guid? DocumentSlotId,
    string FileName,
    string? MimeType,
    long? FileSizeBytes,
    DocumentStage SchemeStage,
    string UploadStatus,
    DocumentStatus ProcessingStatus,
    string? ProcessingFailureReason,
    Guid? UploadedBy,
    DateTimeOffset UploadedAt,
    DateTimeOffset? UploadConfirmedAt,
    DateTimeOffset? ProcessedAt,
    DateTimeOffset? DeletedAt,
    DocumentClassificationDto? Classification);

/// <summary>A case's checklist slots (with fulfillment) and all its live documents, newest first.</summary>
public sealed record CaseDocumentsDto(IReadOnlyList<DocumentSlotDto> Slots, IReadOnlyList<DocumentDto> Documents);

/// <summary>
/// Declares an upload. <c>SchemeStage</c> is declared by the client and recorded immutably (guide §8 #5 decision,
/// 2026-10-03). <c>DocumentSlotId</c> is optional and must belong to the case.
/// </summary>
public sealed record CreateDocumentUploadRequest(Guid? DocumentSlotId, string? FileName, string? MimeType, long? FileSizeBytes, DocumentStage? SchemeStage);

/// <summary>
/// A one-time pre-signed PUT: send the bytes straight to <c>UploadUrl</c> with <c>RequiredHeaders</c> before
/// <c>ExpiresAt</c>, then confirm with <c>POST …/uploaded</c>. A new URL is issued per declaration; never reused.
/// </summary>
public sealed record DocumentUploadTicketDto(
    Guid DocumentId,
    Uri UploadUrl,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string> RequiredHeaders,
    DocumentDto Document);
