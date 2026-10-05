using Chargeback.Api.Features.Documents.Contracts;
using Chargeback.Infrastructure.Ai;
using Chargeback.SharedKernel.Events;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.Api.Features.Documents;

/// <summary>
/// Upload limits. <c>MaxFileSizeBytes</c> (default 25 MB) is a configurable default pending product sign-off. The MIME
/// allowlist is PDF, JPEG, PNG and TIFF. Each pre-signed URL is valid for <c>UploadUrlTtl</c> (default 15 minutes).
/// </summary>
public sealed class DocumentOptions
{
    public const string SectionName = "Documents";

    public static readonly IReadOnlyList<string> DefaultAllowedMimeTypes = ["application/pdf", "image/jpeg", "image/png", "image/tiff"];

    public long MaxFileSizeBytes { get; set; } = 25L * 1024 * 1024;

    public TimeSpan UploadUrlTtl { get; set; } = TimeSpan.FromMinutes(15);

    public List<string> AllowedMimeTypes { get; set; } = [.. DefaultAllowedMimeTypes];
}

/// <summary>Status guards and slot fulfillment. The database trigger (migration 0007) enforces immutability independently.</summary>
public static class DocumentLifecycle
{
    /// <summary>A declared upload can be confirmed once; confirming an UPLOADED document again is a no-op.</summary>
    public static bool CanConfirmUpload(string uploadStatus, bool deleted) =>
        !deleted && uploadStatus is DocumentUploadStatuses.PendingUpload or DocumentUploadStatuses.Uploaded;

    /// <summary>Soft delete only before processing starts: PENDING_UPLOAD, or UPLOADED while processing is still Pending.</summary>
    public static bool CanDelete(string uploadStatus, DocumentStatus processing, bool deleted) =>
        !deleted && processing == DocumentStatus.Pending
        && uploadStatus is DocumentUploadStatuses.PendingUpload or DocumentUploadStatuses.Uploaded;

    /// <summary>Processing starts once, for a live confirmed upload that has not been processed.</summary>
    public static bool CanStartProcessing(string uploadStatus, DocumentStatus processing, bool deleted) =>
        !deleted && uploadStatus == DocumentUploadStatuses.Uploaded && processing == DocumentStatus.Pending;

    /// <param name="liveUploadStatuses">Upload statuses of the slot's documents that are not soft-deleted.</param>
    public static SlotFulfillment Fulfillment(IEnumerable<string> liveUploadStatuses)
    {
        ArgumentNullException.ThrowIfNull(liveUploadStatuses);
        var statuses = liveUploadStatuses.ToArray();
        return statuses.Contains(DocumentUploadStatuses.Uploaded, StringComparer.Ordinal) ? SlotFulfillment.Uploaded
            : statuses.Length > 0 ? SlotFulfillment.AwaitingUpload
            : SlotFulfillment.Missing;
    }

    /// <summary>Failure reason recorded when the classification capability does not succeed (no retry).</summary>
    public static string FailureReason(AiResultStatus status) => status switch
    {
        AiResultStatus.Disabled => "AI_DISABLED",
        AiResultStatus.Unavailable => "AI_UNAVAILABLE",
        _ => "AI_INVALID_OUTPUT",
    };

    /// <summary>The S3 object key. Built from ids only: no file name or other user input reaches the key.</summary>
    public static string S3Key(Guid caseId, Guid documentId) => $"cases/{caseId:N}/documents/{documentId:N}";
}

public static class DocumentErrors
{
    public static readonly Error SlotNotInCase = Error.Unprocessable(
        "DOCUMENT_SLOT_NOT_IN_CASE", "documentSlotId must be one of this case's document slots.");

    public static readonly Error UploadNotFound = Error.Unprocessable(
        "UPLOAD_NOT_FOUND", "The file has not been uploaded to the pre-signed URL yet. Upload it, then confirm.");

    public static readonly Error NotDeletable = Error.Conflict(
        "DOCUMENT_NOT_DELETABLE", "A document can be deleted only before processing starts (PENDING_UPLOAD, or UPLOADED with processing Pending).");

    public static readonly Error AlreadyProcessed = Error.Conflict(
        "EVENT_ALREADY_PROCESSED", "This workflow event has already been processed.");
}

/// <summary>An upload was declared and a pre-signed URL issued.</summary>
public sealed record DocumentUploadRequested : DomainEvent
{
    public override string EventType => "document.upload.requested";

    public required Guid DocumentId { get; init; }

    public required Guid? DocumentSlotId { get; init; }

    public required string MimeType { get; init; }

    public required long FileSizeBytes { get; init; }

    public required DocumentStage SchemeStage { get; init; }

    public required Guid RequestedBy { get; init; }
}

/// <summary>The client confirmed the upload; triggers asynchronous OCR/classification (outbox).</summary>
public sealed record DocumentUploaded : DomainEvent
{
    public override string EventType => "document.uploaded";

    public required Guid DocumentId { get; init; }

    public required Guid? DocumentSlotId { get; init; }

    public required DocumentStage SchemeStage { get; init; }

    public required Guid ConfirmedBy { get; init; }
}

/// <summary>Processing finished (Success or Failed, with the classification run that produced it).</summary>
public sealed record DocumentProcessed : DomainEvent
{
    public override string EventType => "document.processed";

    public required Guid DocumentId { get; init; }

    public required Guid ClassificationId { get; init; }

    public required DocumentStatus ProcessingStatus { get; init; }

    public required string? FailureReason { get; init; }
}

public sealed record DocumentDeleted : DomainEvent
{
    public override string EventType => "document.deleted";

    public required Guid DocumentId { get; init; }

    public required Guid DeletedBy { get; init; }
}
