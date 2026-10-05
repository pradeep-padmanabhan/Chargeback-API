using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Documents.Contracts;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using MediatR;

namespace Chargeback.Api.Features.Documents.Queries;

[RequirePermission(Permissions.ViewDocuments)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record GetCaseDocumentsQuery(Guid CaseId) : IQuery<CaseDocumentsDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

[RequirePermission(Permissions.ViewDocuments)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record GetCaseDocumentQuery(Guid CaseId, Guid DocumentId) : IQuery<DocumentDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

internal sealed class GetCaseDocumentsHandler(IDocumentChecklistReader checklist, DocumentReader documents)
    : IRequestHandler<GetCaseDocumentsQuery, Result<CaseDocumentsDto>>
{
    public async Task<Result<CaseDocumentsDto>> Handle(GetCaseDocumentsQuery request, CancellationToken cancellationToken) =>
        new CaseDocumentsDto(
            await checklist.ReadForCaseAsync(request.CaseId, cancellationToken),
            await documents.ListLiveAsync(request.CaseId, cancellationToken));
}

internal sealed class GetCaseDocumentHandler(DocumentReader documents) : IRequestHandler<GetCaseDocumentQuery, Result<DocumentDto>>
{
    public async Task<Result<DocumentDto>> Handle(GetCaseDocumentQuery request, CancellationToken cancellationToken) =>
        await documents.ReadAsync(request.CaseId, request.DocumentId, cancellationToken) is { } dto ? dto : Errors.ResourceNotFound;
}
