using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Documents.Contracts;
using Chargeback.Api.Features.Documents.Queries;
using Chargeback.Infrastructure.Persistence;
using Chargeback.Infrastructure.Persistence.Entities;
using Chargeback.Infrastructure.Security;
using Chargeback.Infrastructure.Storage;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using Chargeback.SharedKernel.ValueObjects;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chargeback.Api.Features.Documents.Upload;

/// <summary>
/// Declares an upload and returns a one-time pre-signed PUT URL; the API never receives the file. Not transactional:
/// the URL is issued first (an S3 operation), then the document row and its event are saved together.
/// Processor and admin users only until bank-user upload is decided (guide §8).
/// </summary>
[RequirePermission(Permissions.UploadDocument)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record CreateDocumentUploadCommand(Guid CaseId, CreateDocumentUploadRequest Body) : ICommand<DocumentUploadTicketDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

/// <summary>The client finished the S3 upload. Idempotent: confirming an UPLOADED document returns it unchanged.</summary>
[RequirePermission(Permissions.UploadDocument)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record ConfirmDocumentUploadCommand(Guid CaseId, Guid DocumentId) : ICommand<DocumentDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

/// <summary>Soft delete (sets <c>deletedAt</c>), allowed only before processing starts.</summary>
[RequirePermission(Permissions.UploadDocument)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record DeleteDocumentCommand(Guid CaseId, Guid DocumentId) : ICommand<DocumentDto>, IResourceScopedRequest, ITransactionalCommand
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

public sealed class CreateDocumentUploadValidator : AbstractValidator<CreateDocumentUploadCommand>
{
    public const int MaxFileNameLength = 255;

    public CreateDocumentUploadValidator(IOptions<DocumentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var limits = options.Value;
        RuleFor(x => x.Body).NotNull();
        When(x => x.Body is not null, () =>
        {
            RuleFor(x => x.Body.FileName)
                .NotEmpty()
                .MaximumLength(MaxFileNameLength)
                .Must(n => n is null || (!n.Contains('/', StringComparison.Ordinal) && !n.Contains('\\', StringComparison.Ordinal) && !n.Any(char.IsControl)))
                .WithMessage("fileName must be a plain file name (no path separators or control characters).")
                .Must(n => !PanRedactor.ContainsPan(n)).WithMessage("Must not contain a card number.")
                .OverridePropertyName("fileName");
            RuleFor(x => x.Body.MimeType)
                .NotEmpty()
                .Must(m => m is not null && limits.AllowedMimeTypes.Contains(m.Trim(), StringComparer.OrdinalIgnoreCase))
                .WithMessage($"mimeType must be one of: {string.Join(", ", limits.AllowedMimeTypes)}.")
                .OverridePropertyName("mimeType");
            RuleFor(x => x.Body.FileSizeBytes)
                .NotNull()
                .InclusiveBetween(1, limits.MaxFileSizeBytes)
                .WithMessage($"fileSizeBytes must be between 1 and {limits.MaxFileSizeBytes}.")
                .OverridePropertyName("fileSizeBytes");
            RuleFor(x => x.Body.SchemeStage)
                .NotNull().WithMessage("schemeStage is required: Initial, PreArbitration or Arbitration.")
                .IsInEnum()
                .OverridePropertyName("schemeStage");
        });
    }
}

internal sealed class CreateDocumentUploadHandler(
    ChargebackDbContext db, IS3Service s3, DocumentReader documents, ICurrentUser currentUser, IOptions<DocumentOptions> options, TimeProvider timeProvider)
    : IRequestHandler<CreateDocumentUploadCommand, Result<DocumentUploadTicketDto>>
{
    public async Task<Result<DocumentUploadTicketDto>> Handle(CreateDocumentUploadCommand request, CancellationToken cancellationToken)
    {
        var bankId = await (from c in db.Cases
                            join d in db.Disputes on c.DisputeId equals d.Id
                            where c.Id == request.CaseId
                            select (Guid?)d.BankId).SingleOrDefaultAsync(cancellationToken);
        if (bankId is null)
        {
            return Errors.ResourceNotFound;
        }

        var body = request.Body;
        if (body.DocumentSlotId is { } slotId && !await db.DocumentSlots.AnyAsync(s => s.Id == slotId && s.CaseId == request.CaseId, cancellationToken))
        {
            return DocumentErrors.SlotNotInCase;
        }

        var mimeType = body.MimeType!.Trim().ToLowerInvariant();
        var document = new Document
        {
            CaseId = request.CaseId,
            DocumentSlotId = body.DocumentSlotId,
            FileName = body.FileName!.Trim(),
            S3Key = "",
            MimeType = mimeType,
            FileSizeBytes = body.FileSizeBytes!.Value,
            SchemeStage = body.SchemeStage!.Value,
            DocumentStatus = DocumentStatus.Pending,
            UploadStatus = DocumentUploadStatuses.PendingUpload,
            UploadedBy = currentUser.UserId,
            UploadedAt = timeProvider.GetUtcNow(),
        };
        document.S3Key = DocumentLifecycle.S3Key(request.CaseId, document.Id);

        // A fresh URL per declaration (never reused), issued before anything is saved.
        var upload = await s3.CreatePresignedPutAsync(document.S3Key, mimeType, document.FileSizeBytes.Value, options.Value.UploadUrlTtl, cancellationToken);

        document.AddDomainEvent(new DocumentUploadRequested
        {
            CaseId = request.CaseId,
            BankId = bankId,
            DocumentId = document.Id,
            DocumentSlotId = document.DocumentSlotId,
            MimeType = mimeType,
            FileSizeBytes = document.FileSizeBytes.Value,
            SchemeStage = document.SchemeStage,
            RequestedBy = currentUser.UserId,
        });
        db.Documents.Add(document);
        await db.SaveChangesAsync(cancellationToken);

        var dto = (await documents.ReadAsync(request.CaseId, document.Id, cancellationToken))!;
        return new DocumentUploadTicketDto(document.Id, upload.Url, upload.ExpiresAt, upload.RequiredHeaders, dto);
    }
}

internal sealed class ConfirmDocumentUploadHandler(ChargebackDbContext db, IS3Service s3, DocumentReader documents, ICurrentUser currentUser, TimeProvider timeProvider)
    : IRequestHandler<ConfirmDocumentUploadCommand, Result<DocumentDto>>
{
    public async Task<Result<DocumentDto>> Handle(ConfirmDocumentUploadCommand request, CancellationToken cancellationToken)
    {
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == request.DocumentId && d.CaseId == request.CaseId, cancellationToken);
        if (document is null || !DocumentLifecycle.CanConfirmUpload(document.UploadStatus, document.DeletedAt is not null))
        {
            return Errors.ResourceNotFound;
        }

        if (document.UploadStatus == DocumentUploadStatuses.PendingUpload)
        {
            // External HEAD, outside any database transaction.
            if (!await s3.ObjectExistsAsync(document.S3Key, cancellationToken))
            {
                return DocumentErrors.UploadNotFound;
            }

            var bankId = await (from c in db.Cases
                                join d in db.Disputes on c.DisputeId equals d.Id
                                where c.Id == document.CaseId
                                select d.BankId).SingleAsync(cancellationToken);
            document.UploadStatus = DocumentUploadStatuses.Uploaded;
            document.UploadConfirmedAt = timeProvider.GetUtcNow();
            document.AddDomainEvent(new DocumentUploaded
            {
                CaseId = document.CaseId,
                BankId = bankId,
                DocumentId = document.Id,
                DocumentSlotId = document.DocumentSlotId,
                SchemeStage = document.SchemeStage,
                ConfirmedBy = currentUser.UserId,
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A concurrent confirmation or delete won; report the current state.
                var current = await documents.ReadAsync(request.CaseId, request.DocumentId, cancellationToken);
                return current is { DeletedAt: null } ? current : Errors.ResourceNotFound;
            }
        }

        return (await documents.ReadAsync(request.CaseId, document.Id, cancellationToken))!;
    }
}

internal sealed class DeleteDocumentHandler(ChargebackDbContext db, DocumentReader documents, ICurrentUser currentUser, TimeProvider timeProvider)
    : IRequestHandler<DeleteDocumentCommand, Result<DocumentDto>>
{
    public async Task<Result<DocumentDto>> Handle(DeleteDocumentCommand request, CancellationToken cancellationToken)
    {
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == request.DocumentId && d.CaseId == request.CaseId, cancellationToken);
        if (document is null || document.DeletedAt is not null)
        {
            return Errors.ResourceNotFound;
        }

        if (!DocumentLifecycle.CanDelete(document.UploadStatus, document.DocumentStatus, deleted: false))
        {
            return DocumentErrors.NotDeletable;
        }

        var bankId = await (from c in db.Cases
                            join d in db.Disputes on c.DisputeId equals d.Id
                            where c.Id == document.CaseId
                            select d.BankId).SingleAsync(cancellationToken);
        document.DeletedAt = timeProvider.GetUtcNow();
        document.DeletedBy = currentUser.UserId;
        document.AddDomainEvent(new DocumentDeleted { CaseId = document.CaseId, BankId = bankId, DocumentId = document.Id, DeletedBy = currentUser.UserId });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Processing claimed the document in the meantime.
            return DocumentErrors.NotDeletable;
        }

        return (await documents.ReadAsync(request.CaseId, document.Id, cancellationToken))!;
    }
}
