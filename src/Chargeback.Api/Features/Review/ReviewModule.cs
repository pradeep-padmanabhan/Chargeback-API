using Carter;
using Chargeback.Api.Common.Endpoints;
using Chargeback.Api.Common.Http;
using Chargeback.Api.Common.Paging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Features.Review.Contracts;
using Chargeback.Api.Features.Review.Decision;
using Chargeback.Api.Features.Review.Workspace;
using Chargeback.SharedKernel.Paging;
using MediatR;

namespace Chargeback.Api.Features.Review;

/// <summary>
/// Human Review (common guide §6 Act 5). The AI summary is generated once by the workflow when a case enters review
/// and stored; no endpoint generates or regenerates it.
/// </summary>
public sealed class ReviewModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(EndpointConventions.ApiPrefix).WithTags("Review");

        group.MapGet("/reviews/queue", (int? page, int? pageSize, string? sortBy, string? sortDirection, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetReviewQueueQuery(new PageRequest(page, pageSize, sortBy, sortDirection)), http))
            .WithContract<PagedResult<ReviewQueueItemDto>>("getReviewQueue", "Cases in UNDER_REVIEW within the caller's bank scope (REVIEW_CASE)")
            .WithSortFields(GetReviewQueueQuery.Sorts);

        group.MapGet("/cases/{caseId:guid}/review", (Guid caseId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetReviewWorkspaceQuery(caseId), http))
            .WithContract<ReviewWorkspaceDto>("getReviewWorkspace", "Analyst decision workspace (stored AI summary is advisory; never generated on read)");

        group.MapPost("/cases/{caseId:guid}/review/decision", (Guid caseId, ReviewDecisionRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new SubmitReviewDecisionCommand(caseId, body, IdempotencyKey.From(http)), http))
            .WithContract<ReviewDecisionResponse>("submitReviewDecision", "Approve or reject a case under review (REVIEW_CASE; Idempotency-Key required)")
            .WithDescription(
                "Body {decision, rationale, reasonCodeId, expectedVersion}. Approve: reasonCodeId must be the case's derived reason code " +
                "(422 REASON_CODE_NOT_DERIVED / REASON_CODE_MISMATCH); Reject: omit reasonCodeId. Case must be UNDER_REVIEW " +
                "(422 INVALID_TRANSITION). 409 CASE_VERSION_MISMATCH when expectedVersion is stale. Records an append-only decision " +
                "and moves the case to APPROVED or REJECTED.")
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }
}
