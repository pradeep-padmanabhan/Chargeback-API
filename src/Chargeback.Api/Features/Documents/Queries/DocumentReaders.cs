using System.Text.Json;
using Chargeback.Api.Features.Documents.Contracts;
using Chargeback.Infrastructure.Persistence.Queries;
using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.Api.Features.Documents.Queries;

/// <summary>Document rows with their latest classification run.</summary>
internal sealed class DocumentReader(IDapperQueryService db)
{
    private const string Sql = """
        SELECT d.id, d.case_id, d.document_slot_id, d.file_name, d.mime_type, d.file_size_bytes, d.scheme_stage, d.upload_status,
               d.document_status, d.processing_error, d.uploaded_by, d.uploaded_at, d.upload_confirmed_at, d.processed_at, d.deleted_at,
               c.id AS classification_id, c.status AS classification_status, c.category, c.confidence,
               c.extracted_fields::text AS extracted_fields, c.concerns::text AS concerns, c.failure_reason, c.model_name,
               c.created_at AS classified_at
        FROM chargeback_diagram.documents d
        LEFT JOIN LATERAL (
          SELECT x.id, x.status, x.category, x.confidence, x.extracted_fields, x.concerns, x.failure_reason, x.model_name, x.created_at
          FROM chargeback_diagram.document_classifications x
          WHERE x.document_id = d.id
          ORDER BY x.created_at DESC, x.id DESC
          LIMIT 1) c ON true
        WHERE d.case_id = @CaseId
        """;

    /// <summary>Live (not soft-deleted) documents of the case, newest first.</summary>
    public async Task<IReadOnlyList<DocumentDto>> ListLiveAsync(Guid caseId, CancellationToken cancellationToken) =>
        (await db.QueryAsync<Row>(Sql + " AND d.deleted_at IS NULL ORDER BY d.uploaded_at DESC, d.id DESC", new { CaseId = caseId }, cancellationToken))
            .Select(r => r.ToDto())
            .ToArray();

    /// <summary>One document of the case, including a soft-deleted one (its <c>deletedAt</c> is set).</summary>
    public async Task<DocumentDto?> ReadAsync(Guid caseId, Guid documentId, CancellationToken cancellationToken) =>
        (await db.QuerySingleOrDefaultAsync<Row>(Sql + " AND d.id = @DocumentId", new { CaseId = caseId, DocumentId = documentId }, cancellationToken))?.ToDto();

    private sealed class Row
    {
        public Guid Id { get; set; }

        public Guid CaseId { get; set; }

        public Guid? DocumentSlotId { get; set; }

        public string FileName { get; set; } = "";

        public string? MimeType { get; set; }

        public long? FileSizeBytes { get; set; }

        public string SchemeStage { get; set; } = "";

        public string UploadStatus { get; set; } = "";

        public string DocumentStatus { get; set; } = "";

        public string? ProcessingError { get; set; }

        public Guid? UploadedBy { get; set; }

        public DateTimeOffset UploadedAt { get; set; }

        public DateTimeOffset? UploadConfirmedAt { get; set; }

        public DateTimeOffset? ProcessedAt { get; set; }

        public DateTimeOffset? DeletedAt { get; set; }

        public Guid? ClassificationId { get; set; }

        public string? ClassificationStatus { get; set; }

        public string? Category { get; set; }

        public decimal? Confidence { get; set; }

        public string? ExtractedFields { get; set; }

        public string? Concerns { get; set; }

        public string? FailureReason { get; set; }

        public string? ModelName { get; set; }

        public DateTimeOffset? ClassifiedAt { get; set; }

        public DocumentDto ToDto() => new(
            Id,
            CaseId,
            DocumentSlotId,
            FileName,
            MimeType,
            FileSizeBytes,
            Enum.Parse<DocumentStage>(SchemeStage, ignoreCase: true),
            UploadStatus,
            Enum.Parse<DocumentStatus>(DocumentStatus, ignoreCase: true),
            ProcessingError,
            UploadedBy,
            UploadedAt,
            UploadConfirmedAt,
            ProcessedAt,
            DeletedAt,
            ClassificationId is { } id
                ? new DocumentClassificationDto(
                    id,
                    ClassificationStatus!,
                    Category,
                    Confidence,
                    JsonSerializer.Deserialize<string[]>(Concerns ?? "[]") ?? [],
                    JsonDocument.Parse(ExtractedFields ?? "{}").RootElement.Clone(),
                    FailureReason,
                    ModelName,
                    ClassifiedAt!.Value,
                    Advisory: true)
                : null);
    }
}

/// <summary>Read-only checklist: slots with fulfillment computed from their live documents.</summary>
internal sealed class DocumentChecklistReader(IDapperQueryService db) : IDocumentChecklistReader
{
    private const string Sql = """
        SELECT s.id AS slot_id, s.slot_name, s.is_required, s.expected_type,
               d.id AS document_id, d.file_name, d.mime_type, d.file_size_bytes, d.scheme_stage, d.upload_status,
               d.document_status, d.processing_error, d.uploaded_at, d.processed_at
        FROM chargeback_diagram.document_slots s
        LEFT JOIN chargeback_diagram.documents d ON d.document_slot_id = s.id AND d.case_id = s.case_id AND d.deleted_at IS NULL
        WHERE s.case_id = @CaseId
        ORDER BY s.slot_name, s.id, d.uploaded_at DESC, d.id
        """;

    public async Task<IReadOnlyList<DocumentSlotDto>> ReadForCaseAsync(Guid caseId, CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<Row>(Sql, new { CaseId = caseId }, cancellationToken);
        return rows
            .GroupBy(r => r.SlotId)
            .Select(g =>
            {
                var slot = g.First();
                var documents = g.Where(r => r.DocumentId is not null)
                    .Select(r => new DocumentSummaryDto(
                        r.DocumentId!.Value,
                        r.FileName!,
                        r.MimeType,
                        r.FileSizeBytes,
                        Enum.Parse<DocumentStage>(r.SchemeStage!, ignoreCase: true),
                        r.UploadStatus!,
                        Enum.Parse<DocumentStatus>(r.DocumentStatus!, ignoreCase: true),
                        r.ProcessingError,
                        r.UploadedAt!.Value,
                        r.ProcessedAt))
                    .ToArray();
                return new DocumentSlotDto(
                    slot.SlotId, slot.SlotName, slot.IsRequired, slot.ExpectedType,
                    DocumentLifecycle.Fulfillment(documents.Select(d => d.UploadStatus)), documents);
            })
            .ToArray();
    }

    private sealed class Row
    {
        public Guid SlotId { get; set; }

        public string SlotName { get; set; } = "";

        public bool IsRequired { get; set; }

        public string? ExpectedType { get; set; }

        public Guid? DocumentId { get; set; }

        public string? FileName { get; set; }

        public string? MimeType { get; set; }

        public long? FileSizeBytes { get; set; }

        public string? SchemeStage { get; set; }

        public string? UploadStatus { get; set; }

        public string? DocumentStatus { get; set; }

        public string? ProcessingError { get; set; }

        public DateTimeOffset? UploadedAt { get; set; }

        public DateTimeOffset? ProcessedAt { get; set; }
    }
}
