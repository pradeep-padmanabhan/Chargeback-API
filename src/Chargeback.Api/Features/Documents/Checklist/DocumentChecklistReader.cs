using Chargeback.Api.Features.Documents.Contracts;
using Chargeback.Infrastructure.Persistence.Queries;
using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.Api.Features.Documents.Checklist;

public static class DocumentsServiceRegistration
{
    public static IServiceCollection AddDocumentsSlice(this IServiceCollection services)
    {
        services.AddScoped<IDocumentChecklistReader, DocumentChecklistReader>();
        return services;
    }
}

/// <summary>Read-only checklist (Phase 8 owns upload, stage and processing). Scheme stage and processing status stay independent.</summary>
internal sealed class DocumentChecklistReader(IDapperQueryService db) : IDocumentChecklistReader
{
    private const string Sql = """
        SELECT s.id AS slot_id, s.slot_name, s.is_required, s.expected_type,
               d.id AS document_id, d.file_name, d.mime_type, d.file_size_bytes, d.scheme_stage, d.document_status,
               d.uploaded_at, d.processed_at
        FROM chargeback_diagram.document_slots s
        LEFT JOIN chargeback_diagram.documents d ON d.document_slot_id = s.id AND d.case_id = s.case_id
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
                        Enum.Parse<DocumentStatus>(r.DocumentStatus!, ignoreCase: true),
                        r.UploadedAt!.Value,
                        r.ProcessedAt))
                    .ToArray();
                return new DocumentSlotDto(slot.SlotId, slot.SlotName, slot.IsRequired, slot.ExpectedType, documents);
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

        public string? DocumentStatus { get; set; }

        public DateTimeOffset? UploadedAt { get; set; }

        public DateTimeOffset? ProcessedAt { get; set; }
    }
}
