using Carter;
using Chargeback.Api.Common.Endpoints;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Features.Documents.Contracts;
using Chargeback.Api.Features.Documents.Queries;
using Chargeback.Api.Features.Documents.Upload;
using MediatR;

namespace Chargeback.Api.Features.Documents;

public static class DocumentsServiceRegistration
{
    public static IServiceCollection AddDocumentsSlice(this IServiceCollection services)
    {
        services.AddOptions<DocumentOptions>().BindConfiguration(DocumentOptions.SectionName);
        services.AddScoped<DocumentReader>();
        services.AddScoped<IDocumentChecklistReader, DocumentChecklistReader>();
        return services;
    }
}

/// <summary>
/// Evidence &amp; Documents (common guide §6 Act 4). Files go straight to S3 through one-time pre-signed URLs; the API
/// never streams them. OCR/classification runs asynchronously after the upload is confirmed.
/// </summary>
public sealed class DocumentsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"{EndpointConventions.ApiPrefix}/cases/{{caseId:guid}}/documents").WithTags("Document");

        group.MapPost("/", (Guid caseId, CreateDocumentUploadRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new CreateDocumentUploadCommand(caseId, body), http,
                    dto => TypedResults.Created($"/api/v1/cases/{caseId}/documents/{dto.DocumentId}", dto)))
            .WithContract<DocumentUploadTicketDto>("createDocumentUpload", "Declare an upload; returns a one-time pre-signed PUT URL (UPLOAD_DOCUMENT)", StatusCodes.Status201Created)
            .WithDescription(
                "Body {documentSlotId?, fileName, mimeType, fileSizeBytes, schemeStage}. MIME types: PDF, JPEG, PNG, TIFF; size up to the " +
                "configured maximum (default 25 MB). The document starts PENDING_UPLOAD; upload the bytes to uploadUrl with requiredHeaders " +
                "before expiresAt (15 minutes), then POST …/uploaded. 422 DOCUMENT_SLOT_NOT_IN_CASE.")
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{documentId:guid}/uploaded", (Guid caseId, Guid documentId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ConfirmDocumentUploadCommand(caseId, documentId), http))
            .WithContract<DocumentDto>("confirmDocumentUpload", "Confirm the S3 upload; starts asynchronous OCR/classification (UPLOAD_DOCUMENT)")
            .WithDescription("PENDING_UPLOAD → UPLOADED. Idempotent. 422 UPLOAD_NOT_FOUND when the object is not in S3 yet.")
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/", (Guid caseId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetCaseDocumentsQuery(caseId), http))
            .WithContract<CaseDocumentsDto>("listCaseDocuments", "Checklist slots with fulfillment, and live documents with upload and processing status (VIEW_DOCUMENTS)");

        group.MapGet("/{documentId:guid}", (Guid caseId, Guid documentId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetCaseDocumentQuery(caseId, documentId), http))
            .WithContract<DocumentDto>("getCaseDocument", "One document with its latest advisory classification (VIEW_DOCUMENTS)");

        group.MapDelete("/{documentId:guid}", (Guid caseId, Guid documentId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new DeleteDocumentCommand(caseId, documentId), http, _ => TypedResults.NoContent()))
            .WithContract<DocumentDto>("deleteCaseDocument", "Soft-delete a document before processing starts (UPLOAD_DOCUMENT)", StatusCodes.Status204NoContent)
            .WithDescription("Allowed while PENDING_UPLOAD, or UPLOADED with processing Pending; otherwise 409 DOCUMENT_NOT_DELETABLE. The row is kept with deletedAt.")
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
