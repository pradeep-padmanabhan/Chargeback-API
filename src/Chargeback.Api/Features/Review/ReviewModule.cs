using Carter;
using Chargeback.Api.Common.Endpoints;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Paging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Review.Contracts;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Paging;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using MediatR;

namespace Chargeback.Api.Features.Review;

// Phase 4 contract stubs (Phase 9: Human Review). The AI summary is generated once when the case
// enters review and stored; GET never triggers generation.

[RequirePermission(Permissions.ReviewCase)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record GetReviewQueueQuery(PageRequest Page) : IQuery<PagedResult<ReviewQueueItemDto>>, IScopeFilteredRequest, IPagedRequest
{
    /// <summary><c>createdAt</c> is the case's creation time.</summary>
    public static readonly SortMap Sorts = new(
        "c.id", ("createdAt", "c.created_at"), ("caseReference", "c.case_reference"), ("priority", "c.priority"), ("filingDeadlineDate", "c.filing_deadline_date"));

    public SortMap Sort => Sorts;
}

[RequirePermission(Permissions.ReviewCase)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record GetReviewWorkspaceQuery(Guid CaseId) : IQuery<ReviewWorkspaceDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

[RequirePermission(Permissions.ReviewCase)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record SubmitReviewDecisionCommand(Guid CaseId, ReviewDecisionRequest Body) : ICommand<ReviewDecisionResponse>, IResourceScopedRequest, ITransactionalCommand
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

internal sealed class GetReviewQueueHandler : NotImplementedHandler<GetReviewQueueQuery, Result<PagedResult<ReviewQueueItemDto>>>;

internal sealed class GetReviewWorkspaceHandler : NotImplementedHandler<GetReviewWorkspaceQuery, Result<ReviewWorkspaceDto>>;

internal sealed class SubmitReviewDecisionHandler : NotImplementedHandler<SubmitReviewDecisionCommand, Result<ReviewDecisionResponse>>;

public sealed class ReviewModule : ICarterModule
{
    private const string StubPhase = "Phase 9 (Human Review)";

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(EndpointConventions.ApiPrefix).WithTags("Review");

        group.MapGet("/reviews/queue", (int? page, int? pageSize, string? sortBy, string? sortDirection, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetReviewQueueQuery(new PageRequest(page, pageSize, sortBy, sortDirection)), http))
            .WithContract<PagedResult<ReviewQueueItemDto>>("getReviewQueue", "Flagged / policy-selected cases awaiting review")
            .WithSortFields(GetReviewQueueQuery.Sorts)
            .AsStub(StubPhase);

        group.MapGet("/cases/{caseId:guid}/review", (Guid caseId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetReviewWorkspaceQuery(caseId), http))
            .WithContract<ReviewWorkspaceDto>("getReviewWorkspace", "Analyst decision workspace (stored AI summary is advisory)")
            .AsStub(StubPhase);

        group.MapPost("/cases/{caseId:guid}/review/decision", (Guid caseId, ReviewDecisionRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new SubmitReviewDecisionCommand(caseId, body), http))
            .WithContract<ReviewDecisionResponse>("submitReviewDecision", "Approve or reject with rationale (audited)")
            .AsStub(StubPhase);
    }
}
