using System.Text.Json;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Documents.Contracts;
using Chargeback.Infrastructure.Ai;
using Chargeback.Infrastructure.Ai.Capabilities;
using Chargeback.Infrastructure.Persistence;
using Chargeback.Infrastructure.Persistence.Entities;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.ValueObjects;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chargeback.Api.Features.Documents.Processing;

public enum DocumentProcessingOutcome
{
    /// <summary>Classified; processing status Success.</summary>
    Classified,

    /// <summary>The classification capability did not succeed; processing status Failed with a reason. No retry.</summary>
    Failed,

    /// <summary>Not processable any more (deleted, not confirmed, or already processing/processed).</summary>
    Skipped,
}

/// <summary>
/// Asynchronous OCR/classification after an upload is confirmed (common guide §6 Act 4), through the typed Document
/// Verification capability only — never Bedrock directly. OCR (Textract) is not built yet, so the capability receives no
/// extracted text. Not transactional: the capability call happens outside any database transaction. Every run writes a
/// <c>document_classifications</c> row, so reruns never overwrite earlier results.
/// </summary>
[SystemOperation("Document classification is a workflow step after upload confirmation, never a user action.")]
[NotBankScoped("Workflow step for one document chosen by the workflow; it belongs to that document's case and bank.")]
public sealed record ClassifyDocumentCommand(Guid DocumentId, Guid SourceEventId, string Consumer) : ICommand<DocumentProcessingOutcome>;

internal sealed class ClassifyDocumentHandler(ChargebackDbContext db, IDocumentVerifier verifier, TimeProvider timeProvider)
    : IRequestHandler<ClassifyDocumentCommand, Result<DocumentProcessingOutcome>>
{
    public async Task<Result<DocumentProcessingOutcome>> Handle(ClassifyDocumentCommand request, CancellationToken cancellationToken)
    {
        if (await db.ProcessedDomainEvents.AnyAsync(p => p.EventId == request.SourceEventId, cancellationToken))
        {
            return DocumentErrors.AlreadyProcessed;
        }

        // Claim: processing starts only for a live, confirmed, unprocessed document. From here on it cannot be deleted.
        var claimed = await db.Documents
            .Where(d => d.Id == request.DocumentId && d.DeletedAt == null
                && d.UploadStatus == DocumentUploadStatuses.Uploaded && d.DocumentStatus == DocumentStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.DocumentStatus, DocumentStatus.Processing), cancellationToken);

        var outcome = claimed == 1 ? await ClassifyAsync(request.DocumentId, cancellationToken) : DocumentProcessingOutcome.Skipped;

        db.ProcessedDomainEvents.Add(new ProcessedDomainEvent
        {
            EventId = request.SourceEventId,
            Consumer = request.Consumer,
            ProcessedAt = timeProvider.GetUtcNow(),
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return DocumentErrors.AlreadyProcessed;
        }

        return outcome;
    }

    private async Task<DocumentProcessingOutcome> ClassifyAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await db.Documents.SingleAsync(d => d.Id == documentId, cancellationToken);
        var expectedType = document.DocumentSlotId is { } slotId
            ? await db.DocumentSlots.Where(s => s.Id == slotId).Select(s => s.ExpectedType).SingleOrDefaultAsync(cancellationToken)
            : null;
        var bankId = await (from c in db.Cases
                            join d in db.Disputes on c.DisputeId equals d.Id
                            where c.Id == document.CaseId
                            select d.BankId).SingleAsync(cancellationToken);

        var result = await verifier.VerifyAsync(
            new DocumentVerificationInput(document.Id, document.CaseId, bankId, expectedType, document.SchemeStage, document.ExtractedText ?? ""),
            cancellationToken);

        var output = result.IsSuccess && result.Output!.Confidence is >= 0m and <= 1m ? result.Output : null;
        var failureReason = output is null
            ? DocumentLifecycle.FailureReason(result.IsSuccess ? AiResultStatus.InvalidOutput : result.Status)
            : null;
        var now = timeProvider.GetUtcNow();
        var classification = new DocumentClassification
        {
            DocumentId = document.Id,
            Status = output is null ? "FAILED" : "SUCCESS",
            Category = output?.SuggestedType,
            Confidence = output?.Confidence,
            Concerns = JsonSerializer.Serialize(output?.Concerns ?? []),
            FailureReason = failureReason,
            ModelName = output is null ? null : result.ModelName,
            CreatedAt = now,
        };
        db.DocumentClassifications.Add(classification);

        document.DocumentStatus = output is null ? DocumentStatus.Failed : DocumentStatus.Success;
        document.ProcessingError = failureReason;
        document.ProcessedAt = now;
        if (output is not null)
        {
            document.AiClassification = output.SuggestedType;
            document.AiConfidence = output.Confidence;
        }

        document.AddDomainEvent(new DocumentProcessed
        {
            CaseId = document.CaseId,
            BankId = bankId,
            DocumentId = document.Id,
            ClassificationId = classification.Id,
            ProcessingStatus = document.DocumentStatus,
            FailureReason = failureReason,
        });
        return output is null ? DocumentProcessingOutcome.Failed : DocumentProcessingOutcome.Classified;
    }
}
