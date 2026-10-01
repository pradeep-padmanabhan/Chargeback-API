using Carter;
using Chargeback.Api.Common.Endpoints;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Documents.Contracts;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using MediatR;

namespace Chargeback.Api.Features.Documents;

// Phase 4 contract stubs (Phase 8: Evidence & Documents). Upload uses pre-signed S3 URLs so file
// bytes never pass through a database transaction; OCR/classification run asynchronously.

[RequirePermission(Permissions.ViewCases)]
public sealed record GetDocumentSlotsQuery(Guid CaseId) : IQuery<IReadOnlyList<DocumentSlotDto>>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

[RequirePermission(Permissions.UploadDocument)]
public sealed record CreateDocumentUploadCommand(Guid CaseId, CreateDocumentUploadRequest Body) : ICommand<DocumentUploadTicketDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

[RequirePermission(Permissions.UploadDocument)]
public sealed record CompleteDocumentUploadCommand(Guid DocumentId) : ICommand<DocumentDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Document, DocumentId);
}

[RequirePermission(Permissions.ViewCases)]
public sealed record GetDocumentQuery(Guid DocumentId) : IQuery<DocumentDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Document, DocumentId);
}

[RequirePermission(Permissions.UploadDocument)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record ReprocessDocumentCommand(Guid DocumentId) : ICommand<DocumentDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Document, DocumentId);
}

internal sealed class GetDocumentSlotsHandler : NotImplementedHandler<GetDocumentSlotsQuery, Result<IReadOnlyList<DocumentSlotDto>>>;

internal sealed class CreateDocumentUploadHandler : NotImplementedHandler<CreateDocumentUploadCommand, Result<DocumentUploadTicketDto>>;

internal sealed class CompleteDocumentUploadHandler : NotImplementedHandler<CompleteDocumentUploadCommand, Result<DocumentDto>>;

internal sealed class GetDocumentHandler : NotImplementedHandler<GetDocumentQuery, Result<DocumentDto>>;

internal sealed class ReprocessDocumentHandler : NotImplementedHandler<ReprocessDocumentCommand, Result<DocumentDto>>;

public sealed class DocumentsModule : ICarterModule
{
    private const string StubPhase = "Phase 8 (Evidence & Documents)";

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(EndpointConventions.ApiPrefix).WithTags("Document");

        group.MapGet("/cases/{caseId:guid}/document-slots", (Guid caseId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetDocumentSlotsQuery(caseId), http))
            .WithContract<IReadOnlyList<DocumentSlotDto>>("getDocumentSlots", "Case document checklist with uploaded documents")
            .AsStub(StubPhase);

        group.MapPost("/cases/{caseId:guid}/documents", (Guid caseId, CreateDocumentUploadRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new CreateDocumentUploadCommand(caseId, body), http, dto => TypedResults.Created($"/api/v1/documents/{dto.DocumentId}", dto)))
            .WithContract<DocumentUploadTicketDto>("createDocumentUpload", "Register a document and get a pre-signed upload URL", StatusCodes.Status201Created)
            .AsStub(StubPhase);

        group.MapPost("/documents/{documentId:guid}/upload-completion", (Guid documentId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new CompleteDocumentUploadCommand(documentId), http))
            .WithContract<DocumentDto>("completeDocumentUpload", "Confirm the S3 upload; starts asynchronous OCR/classification")
            .AsStub(StubPhase);

        group.MapGet("/documents/{documentId:guid}", (Guid documentId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetDocumentQuery(documentId), http))
            .WithContract<DocumentDto>("getDocument", "Document metadata, stage, processing status and advisory classification")
            .AsStub(StubPhase);

        group.MapPost("/documents/{documentId:guid}/reprocessing", (Guid documentId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ReprocessDocumentCommand(documentId), http))
            .WithContract<DocumentDto>("reprocessDocument", "Retry failed OCR/classification")
            .AsStub(StubPhase);
    }
}
