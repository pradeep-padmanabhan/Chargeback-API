using Carter;
using Chargeback.Api.Common.Endpoints;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.SchemeLifecycle.Contracts;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using MediatR;

namespace Chargeback.Api.Features.SchemeLifecycle;

// Phase 4 contract stub (Phase 10). The 16-queue Mastercom poller is a background worker, not an endpoint.

[RequirePermission(Permissions.ViewCases)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record GetSchemeStagesQuery(Guid CaseId) : IQuery<IReadOnlyList<SchemeStageDto>>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

internal sealed class GetSchemeStagesHandler : NotImplementedHandler<GetSchemeStagesQuery, Result<IReadOnlyList<SchemeStageDto>>>;

public sealed class SchemeLifecycleModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGroup(EndpointConventions.ApiPrefix).WithTags("SchemeLifecycle")
            .MapGet("/cases/{caseId:guid}/scheme-stages", (Guid caseId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetSchemeStagesQuery(caseId), http))
            .WithContract<IReadOnlyList<SchemeStageDto>>("getSchemeStages", "Recorded Mastercom lifecycle transitions")
            .AsStub("Phase 10 (Mastercom simulator + polling)");
    }
}
