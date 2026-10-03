using Carter;
using Chargeback.Api.Common.Endpoints;
using Chargeback.Api.Common.Http;
using Chargeback.Api.Common.Paging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Features.Cases.ChangeCase;
using Chargeback.Api.Features.Cases.Contracts;
using Chargeback.Api.Features.Cases.CreateCase;
using Chargeback.Api.Features.Cases.GetCases;
using Chargeback.SharedKernel.Paging;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Chargeback.Api.Features.Cases;

public static class CasesServiceRegistration
{
    public static IServiceCollection AddCasesSlice(this IServiceCollection services)
    {
        services.AddScoped<ICaseReferenceGenerator, SequenceCaseReferenceGenerator>();
        services.AddScoped<ICaseDetailReader, CaseDetailReader>();
        return services;
    }
}

public sealed class CasesModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"{EndpointConventions.ApiPrefix}/cases").WithTags("Case");

        group.MapGet("/", (string? status, Guid? bankId, int? page, int? pageSize, string? sortBy, string? sortDirection, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ListCasesQuery(status, bankId, new PageRequest(page, pageSize, sortBy, sortDirection)), http))
            .WithContract<PagedResult<CaseSummaryDto>>("listCases", "Cases within the caller's bank scope (analyst queue: status=FLAGGED)")
            .WithSortFields(ListCasesQuery.Sorts);

        group.MapGet("/{caseId:guid}", (Guid caseId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetCaseQuery(caseId), http, dto =>
                {
                    http.Response.Headers.ETag = CaseVersion.ToETag(dto.Version);
                    return TypedResults.Ok(dto);
                }))
            .WithContract<CaseDetailDto>("getCase", "Case detail with the ETag used for If-Match");

        group.MapGet("/{caseId:guid}/timeline", (Guid caseId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetCaseTimelineQuery(caseId), http))
            .WithContract<IReadOnlyList<CaseTimelineEntryDto>>("getCaseTimeline", "Append-only case timeline (domain events for the case, oldest first)");

        group.MapPost("/{caseId:guid}/transitions", (Guid caseId, TransitionCaseRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new TransitionCaseCommand(caseId, body, IdempotencyKey.From(http)), http, dto =>
                {
                    http.Response.Headers.ETag = CaseVersion.ToETag(dto.Version);
                    return TypedResults.Ok(dto);
                }))
            .WithContract<CaseDetailDto>("transitionCase", "Case lifecycle action: START_REVIEW, FLAG, UNFLAG or CLOSE (UPDATE_CASE_STATUS; Idempotency-Key required)")
            .WithDescription(
                "Body {action, rationale, expectedVersion}. The only way to change case status outside the Human Review decision " +
                "(APPROVE/REJECT) and filing confirmation (FILE); those appear in validActions but return 422 INVALID_TRANSITION here. " +
                "Allowed now: NEW/FLAGGED → UNDER_REVIEW (START_REVIEW); REJECTED → CLOSED (CLOSE); FILED → CLOSED (CLOSE, admin only, else 403). " +
                "FLAG and UNFLAG have no approved transitions yet. CLOSE requires a rationale. " +
                "409 CASE_VERSION_MISMATCH when expectedVersion is stale.")
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPatch("/{caseId:guid}/assignment", (Guid caseId, UpdateCaseAssignmentRequest body, [FromHeader(Name = "If-Match")] string? ifMatch, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new UpdateCaseAssignmentCommand(caseId, body, ifMatch), http, dto =>
                {
                    http.Response.Headers.ETag = CaseVersion.ToETag(dto.Version);
                    return TypedResults.Ok(dto);
                }))
            .WithContract<CaseDetailDto>("updateCaseAssignment", "Assign or unassign a case (ASSIGN_CASE; If-Match required)")
            .WithDescription("The assignee must be an active processor/admin user with VIEW_CASES and current scope for the case's bank; otherwise 409 ASSIGNEE_NOT_ELIGIBLE.")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }
}
