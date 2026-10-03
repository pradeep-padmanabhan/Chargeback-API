using Carter;
using Chargeback.Api.Common.Endpoints;
using Chargeback.Api.Common.Paging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Features.ClientPortal.Contracts;
using Chargeback.SharedKernel.Paging;
using MediatR;

namespace Chargeback.Api.Features.ClientPortal;

public static class ClientPortalServiceRegistration
{
    public static IServiceCollection AddClientPortalSlice(this IServiceCollection services)
    {
        services.AddScoped<CaseThread>();
        return services;
    }
}

/// <summary>
/// Client Portal &amp; Communications (common guide §6 Act 7). Bank users get a curated, read-only view of their own bank's
/// cases and a two-way message thread; analysts reply through the internal thread endpoints. Portal notifications are not
/// in the contract yet (ADR-0114); document upload from the portal and email notifications are out of scope.
/// </summary>
public sealed class ClientPortalModule : ICarterModule
{
    private const string StubPhase = "Phase 11 (Client Portal & Communications)";

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var portal = app.MapGroup($"{EndpointConventions.ApiPrefix}/portal").WithTags("ClientPortal");

        portal.MapGet("/cases", (string? status, int? page, int? pageSize, string? sortBy, string? sortDirection, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ListPortalCasesQuery(status, new PageRequest(page, pageSize, sortBy, sortDirection)), http))
            .WithContract<PagedResult<PortalCaseSummaryDto>>("listPortalCases", "Own bank's cases (bank users only)")
            .WithSortFields(ListPortalCasesQuery.Sorts);

        portal.MapGet("/cases/{caseId:guid}", (Guid caseId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetPortalCaseQuery(caseId), http))
            .WithContract<PortalCaseDetailDto>("getPortalCase", "Curated, bank-safe case view (404 for another bank's case)");

        portal.MapGet("/cases/{caseId:guid}/messages", (Guid caseId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ListPortalMessagesQuery(caseId), http))
            .WithContract<IReadOnlyList<PortalMessageDto>>("listPortalMessages", "Case message thread, oldest first (bank users; poll for updates)");

        portal.MapPost("/cases/{caseId:guid}/messages", (Guid caseId, PostMessageRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new PostPortalMessageCommand(caseId, body), http,
                    dto => TypedResults.Created($"/api/v1/portal/cases/{caseId}/messages", dto)))
            .WithContract<PortalMessageDto>("postPortalMessage", "Bank user posts a message (1-2000 characters)", StatusCodes.Status201Created);

        portal.MapPost("/cases/{caseId:guid}/support-ticket", (Guid caseId, SupportTicketRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new CreateSupportTicketCommand(caseId, body), http, dto => TypedResults.Accepted((string?)null, dto)))
            .WithContract<SupportTicketAcceptedDto>("createSupportTicket", "Raise a support ticket (KNOWN_LIMITATION_ZENDESK_: returns STUB-<uuid>)", StatusCodes.Status202Accepted);

        var thread = app.MapGroup($"{EndpointConventions.ApiPrefix}/cases").WithTags("ClientPortal");

        thread.MapGet("/{caseId:guid}/messages", (Guid caseId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ListCaseMessagesQuery(caseId), http))
            .WithContract<IReadOnlyList<CaseMessageDto>>("listCaseMessages", "Case message thread for analysts (VIEW_CASES)");

        thread.MapPost("/{caseId:guid}/messages", (Guid caseId, PostMessageRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new PostCaseMessageCommand(caseId, body), http, dto => TypedResults.Created($"/api/v1/cases/{caseId}/messages", dto)))
            .WithContract<CaseMessageDto>("postCaseMessage", "Analyst replies on the case thread (VIEW_CASES)", StatusCodes.Status201Created);

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
