using Carter;
using Chargeback.Api.Common.Endpoints;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Sdk.Contracts;

namespace Chargeback.Api.Features.Sdk;

// Contract only. SDK authentication (host-app token exchange) is awaiting confirmation (ADR-0005),
// so these routes sit behind a scheme that authenticates nobody: every call returns 401.
// They deliberately do not reach MediatR until the SDK principal model is approved.
public sealed class SdkModule : ICarterModule
{
    private const string StubPhase = "after ADR-0005 approval (SDK host-app token exchange)";

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var sdk = app.MapGroup($"{EndpointConventions.ApiPrefix}/sdk")
            .WithTags("SDK")
            .RequireAuthorization(ChargebackAuthentication.SdkHostTokenPolicy);

        sdk.MapPost("/sessions", NotImplemented("CreateSdkSession"))
            .WithContract<SdkSessionDto>("createSdkSession", "Open an SDK session from a bank-signed host token", StatusCodes.Status201Created)
            .AsStub(StubPhase);

        sdk.MapPost("/sessions/{sessionId:guid}/turns", (Guid sessionId, SdkTurnRequest body, HttpContext http) =>
                ResultHttpMapper.ToProblem(Errors.NotImplemented("SdkConversationTurn"), http))
            .WithContract<SdkTurnResponse>("postSdkTurn", "Conversational guided-intake turn")
            .AsStub(StubPhase);

        sdk.MapPost("/sessions/{sessionId:guid}/eligibility-check", (Guid sessionId, HttpContext http) =>
                ResultHttpMapper.ToProblem(Errors.NotImplemented("SdkEligibilityCheck"), http))
            .WithContract<SdkEligibilityResponse>("checkSdkEligibility", "Deterministic eligibility pre-check (does not replace the ten gates)")
            .AsStub(StubPhase);

        sdk.MapPost("/sessions/{sessionId:guid}/submission", (Guid sessionId, HttpContext http) =>
                ResultHttpMapper.ToProblem(Errors.NotImplemented("SdkSubmission"), http))
            .WithContract<SdkSubmissionResponse>("submitSdkDispute", "Submit the structured draft as a dispute (channel SDK)", StatusCodes.Status202Accepted)
            .AsStub(StubPhase);
    }

    private static Func<HttpContext, IResult> NotImplemented(string operation) =>
        http => ResultHttpMapper.ToProblem(Errors.NotImplemented(operation), http);
}
