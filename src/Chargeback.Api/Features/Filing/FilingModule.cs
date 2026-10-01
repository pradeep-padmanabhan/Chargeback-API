using System.Text.Json.Serialization;
using Carter;
using Chargeback.Api.Common.Endpoints;
using Chargeback.Api.Common.Http;
using Chargeback.Api.Common.Idempotency;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Filing.Contracts;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using MediatR;

namespace Chargeback.Api.Features.Filing;

// Phase 4 contract stubs (Phase 10: Mastercom simulator). Mastercom endpoints are not invented:
// the gateway is built behind a simulator until the external contract is approved (Q12).

[RequirePermission(Permissions.SubmitMastercom)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record PrepareFilingCommand(Guid CaseId) : ICommand<FilingDraftDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

/// <summary>
/// Not an <see cref="ITransactionalCommand"/>: submission calls Mastercom, which must never run inside a
/// DB transaction. The handler records intent, commits, then submits (query-before-retry on timeout).
/// </summary>
[RequirePermission(Permissions.SubmitMastercom)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
[Idempotent("confirmFiling", IdempotencyMode.Reservation, StatusCodes.Status202Accepted)]
public sealed record ConfirmFilingCommand(Guid FilingId, [property: JsonIgnore] string? IdempotencyKey, ConfirmFilingRequest Body)
    : ICommand<FilingDto>, IResourceScopedRequest, IIdempotentCommand
{
    public ScopedResource Resource => new(ScopedResourceKind.Filing, FilingId);
}

[RequirePermission(Permissions.ViewFilings)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record GetFilingQuery(Guid FilingId) : IQuery<FilingDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Filing, FilingId);
}

[RequirePermission(Permissions.ViewFilings)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record GetFilingApiLogQuery(Guid FilingId) : IQuery<IReadOnlyList<FilingApiLogEntryDto>>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Filing, FilingId);
}

internal sealed class PrepareFilingHandler : NotImplementedHandler<PrepareFilingCommand, Result<FilingDraftDto>>;

internal sealed class ConfirmFilingHandler : NotImplementedHandler<ConfirmFilingCommand, Result<FilingDto>>;

internal sealed class GetFilingHandler : NotImplementedHandler<GetFilingQuery, Result<FilingDto>>;

internal sealed class GetFilingApiLogHandler : NotImplementedHandler<GetFilingApiLogQuery, Result<IReadOnlyList<FilingApiLogEntryDto>>>;

public sealed class FilingModule : ICarterModule
{
    private const string StubPhase = "Phase 10 (Mastercom simulator + human-confirmed filing)";

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(EndpointConventions.ApiPrefix).WithTags("Filing");

        group.MapPost("/cases/{caseId:guid}/filings", (Guid caseId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new PrepareFilingCommand(caseId), http, dto => TypedResults.Created($"/api/v1/filings/{dto.FilingId}", dto)))
            .WithContract<FilingDraftDto>("prepareFiling", "Build the deterministic filing payload for analyst review (does not submit)", StatusCodes.Status201Created)
            .AsStub(StubPhase);

        group.MapPost("/filings/{filingId:guid}/confirmation", (Guid filingId, ConfirmFilingRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(
                    sender,
                    new ConfirmFilingCommand(filingId, IdempotencyKey.From(http), body),
                    http,
                    dto => TypedResults.Accepted($"/api/v1/filings/{dto.Id}", dto)))
            .WithContract<FilingDto>("confirmFiling", "Explicit human confirmation; submits to Mastercom", StatusCodes.Status202Accepted)
            .RequireIdempotencyKey()
            .AsStub(StubPhase);

        group.MapGet("/filings/{filingId:guid}", (Guid filingId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetFilingQuery(filingId), http))
            .WithContract<FilingDto>("getFiling", "Filing status and current scheme stage")
            .AsStub(StubPhase);

        group.MapGet("/filings/{filingId:guid}/api-log", (Guid filingId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetFilingApiLogQuery(filingId), http))
            .WithContract<IReadOnlyList<FilingApiLogEntryDto>>("getFilingApiLog", "Per-filing Mastercom request/response log (masked)")
            .AsStub(StubPhase);
    }
}
