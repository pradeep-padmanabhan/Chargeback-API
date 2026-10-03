using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.Api.Features.Documents.Contracts;

/// <summary>AI classification (<c>documents.ai_classification</c>/<c>ai_confidence</c>). Advisory; upload success ≠ verification.</summary>
public sealed record AdvisoryClassificationDto(string Label, decimal? Confidence, bool Advisory);

/// <summary>Scheme stage and processing status are independent fields (common guide §6 Act 4).</summary>
public sealed record DocumentSummaryDto(
    Guid Id,
    string FileName,
    string? MimeType,
    long? FileSizeBytes,
    DocumentStage SchemeStage,
    DocumentStatus DocumentStatus,
    DateTimeOffset UploadedAt,
    DateTimeOffset? ProcessedAt);

/// <summary>Reads a case's document checklist: slots in name order, each with its documents (newest first).</summary>
public interface IDocumentChecklistReader
{
    Task<IReadOnlyList<DocumentSlotDto>> ReadForCaseAsync(Guid caseId, CancellationToken cancellationToken);
}

/// <summary>Case checklist slot, snapshotted when the case is created.</summary>
public sealed record DocumentSlotDto(Guid Id, string SlotName, bool IsRequired, string? ExpectedType, IReadOnlyList<DocumentSummaryDto> Documents);

public sealed record DocumentDto(
    Guid Id,
    Guid CaseId,
    Guid? DocumentSlotId,
    string FileName,
    string? MimeType,
    long? FileSizeBytes,
    DocumentStage SchemeStage,
    DocumentStatus DocumentStatus,
    string? UploadSource,
    Guid? UploadedBy,
    DateTimeOffset UploadedAt,
    DateTimeOffset? ProcessedAt,
    string? ProcessingError,
    AdvisoryClassificationDto? AiClassification);

/// <summary>
/// Requests a pre-signed S3 upload. Whether <c>SchemeStage</c> is client-supplied or derived from the
/// case state is open (Q13); the server may reject or override it.
/// </summary>
public sealed record CreateDocumentUploadRequest(Guid? DocumentSlotId, string FileName, string MimeType, long FileSizeBytes, DocumentStage? SchemeStage);

public sealed record DocumentUploadTicketDto(Guid DocumentId, Uri UploadUrl, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> RequiredHeaders);
