using Carter;
using Chargeback.Api.Common.Endpoints;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.ClientPortal.Contracts;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Paging;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using MediatR;

namespace Chargeback.Api.Features.ClientPortal;

// Phase 4 contract stubs (Phase 11: Client Portal & Communications).
// Portal notifications have no baseline table and are not in the contract yet (ADR-0114).

[RequirePermission(Permissions.ViewCases)]
[RestrictToUserTypes(UserType.Bank)]
public sealed record ListPortalCasesQuery(PageRequest Page) : IQuery<PagedResult<PortalCaseSummaryDto>>, IScopeFilteredRequest;

[RequirePermission(Permissions.ViewCases)]
[RestrictToUserTypes(UserType.Bank)]
public sealed record GetPortalCaseQuery(Guid CaseId) : IQuery<PortalCaseDetailDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

/// <summary>Case message thread; used by bank users (portal) and processor analysts.</summary>
[RequirePermission(Permissions.ViewCases)]
public sealed record ListCaseMessagesQuery(Guid CaseId) : IQuery<IReadOnlyList<PortalMessageDto>>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

/// <summary>Sender type is taken from the authenticated user, never from the request.</summary>
[RequirePermission(Permissions.SendPortalMessage)]
public sealed record PostCaseMessageCommand(Guid CaseId, PostMessageRequest Body) : ICommand<PortalMessageDto>, IResourceScopedRequest, ITransactionalCommand
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

internal sealed class ListPortalCasesHandler : NotImplementedHandler<ListPortalCasesQuery, Result<PagedResult<PortalCaseSummaryDto>>>;

internal sealed class GetPortalCaseHandler : NotImplementedHandler<GetPortalCaseQuery, Result<PortalCaseDetailDto>>;

internal sealed class ListCaseMessagesHandler : NotImplementedHandler<ListCaseMessagesQuery, Result<IReadOnlyList<PortalMessageDto>>>;

internal sealed class PostCaseMessageHandler : NotImplementedHandler<PostCaseMessageCommand, Result<PortalMessageDto>>;

public sealed class ClientPortalModule : ICarterModule
{
    private const string StubPhase = "Phase 11 (Client Portal & Communications)";

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var portal = app.MapGroup($"{EndpointConventions.ApiPrefix}/portal").WithTags("ClientPortal");

        portal.MapGet("/cases", (int? page, int? pageSize, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ListPortalCasesQuery(new PageRequest(page, pageSize)), http))
            .WithContract<PagedResult<PortalCaseSummaryDto>>("listPortalCases", "Own bank's cases (bank users)")
            .AsStub(StubPhase);

        portal.MapGet("/cases/{caseId:guid}", (Guid caseId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetPortalCaseQuery(caseId), http))
            .WithContract<PortalCaseDetailDto>("getPortalCase", "Curated, bank-safe case view")
            .AsStub(StubPhase);

        var messages = app.MapGroup(EndpointConventions.ApiPrefix).WithTags("ClientPortal");

        messages.MapGet("/cases/{caseId:guid}/messages", (Guid caseId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ListCaseMessagesQuery(caseId), http))
            .WithContract<IReadOnlyList<PortalMessageDto>>("listCaseMessages", "Two-way case message thread")
            .AsStub(StubPhase);

        messages.MapPost("/cases/{caseId:guid}/messages", (Guid caseId, PostMessageRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new PostCaseMessageCommand(caseId, body), http, dto => TypedResults.Created($"/api/v1/cases/{caseId}/messages", dto)))
            .WithContract<PortalMessageDto>("postCaseMessage", "Post a message to the case thread", StatusCodes.Status201Created)
            .AsStub(StubPhase);

        // Anonymous at the HTTP layer by design: authenticity comes from the Zendesk HMAC signature and
        // replay protection (ADR-0107), verified before anything is persisted.
        app.MapPost($"{EndpointConventions.ApiPrefix}/webhooks/zendesk", (HttpContext http) =>
                ResultHttpMapper.ToProblem(Errors.NotImplemented("ZendeskWebhook"), http))
            .WithTags("ClientPortal")
            .WithName("receiveZendeskWebhook")
            .WithSummary("Signed Zendesk event webhook (HMAC + replay protection)")
            .AllowAnonymous()
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .AsStub(StubPhase);
    }
}
