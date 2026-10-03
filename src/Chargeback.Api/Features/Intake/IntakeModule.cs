using Carter;
using Chargeback.Api.Common.Endpoints;
using Chargeback.Api.Common.Http;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Api.Features.Intake.Gates;
using Chargeback.Api.Features.Intake.GetDispute;
using Chargeback.Api.Features.Intake.SubmitDispute;
using Chargeback.SharedKernel.Results;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Chargeback.Api.Features.Intake;

// Monitored-email intake is a background worker (SES/S3 → SQS), not an HTTP endpoint.
// Bulk intake stays a stub until the CSV/Excel template and batch storage are approved (ADR-0112).

[RequirePermission(Permissions.CreateDispute)]
public sealed record SubmitBulkIntakeCommand(Guid BankId, bool DryRun, string FileName, long FileSizeBytes)
    : ICommand<BulkIntakeReportDto>, IBankScopedRequest;

internal sealed class SubmitBulkIntakeHandler : NotImplementedHandler<SubmitBulkIntakeCommand, Result<BulkIntakeReportDto>>;

public static class IntakeServiceRegistration
{
    /// <summary>Slice-owned services. Gate evaluators are registered here once their criteria are approved.</summary>
    public static IServiceCollection AddIntakeSlice(this IServiceCollection services)
    {
        services.AddOptions<GateOptions>().BindConfiguration(GateOptions.SectionName);
        services.AddSingleton<IGateRegistry, ApprovedGateRegistry>();
        services.AddScoped<IGateEngine, GateEngine>();
        services.AddScoped<IDisputeReader, GetDispute.DisputeReader>();
        return services;
    }
}

public sealed class IntakeModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(EndpointConventions.ApiPrefix).WithTags("Intake");

        group.MapPost("/intake/disputes", (SubmitDisputeRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new SubmitDisputeCommand(body, IdempotencyKey.From(http)), http, dto => TypedResults.Accepted($"/api/v1/disputes/{dto.DisputeId}", dto)))
            .WithContract<DisputeAcceptedResponse>("submitDispute", "Submit one dispute (portal form); runs the ten gates → NEW or FLAGGED", StatusCodes.Status202Accepted)
            .WithDescription(
                "Card numbers must be masked to the last four digits; card numbers in any other field are rejected. " +
                "All ten approved gates run and are recorded. Gates whose criteria are not yet signed off are recorded " +
                "with passed = null (PENDING_DEFINITION), which leaves the dispute FLAGGED for analyst review. " +
                "Idempotent (ADR-0106): retrying with the same Idempotency-Key and request within 90 days replays the stored " +
                "202 response (header Idempotent-Replayed: true) and never creates a second dispute.")
            .RequireIdempotencyKey();

        group.MapPost("/intake/bulk", ([FromForm] Guid bankId, IFormFile file, [FromQuery] bool? dryRun, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new SubmitBulkIntakeCommand(bankId, dryRun ?? true, file.FileName, file.Length), http))
            .WithContract<BulkIntakeReportDto>("submitBulkIntake", "CSV/Excel bulk intake; dryRun defaults to true")
            .DisableAntiforgery()
            .RequireIdempotencyKey()
            .AsStub("after ADR-0112 (bulk template + batch storage)");

        group.MapGet("/disputes/{disputeId:guid}", (Guid disputeId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetDisputeQuery(disputeId), http))
            .WithContract<DisputeDto>("getDispute", "One dispute (masked card data only)");

        group.MapGet("/disputes/{disputeId:guid}/gate-results", (Guid disputeId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetDisputeGatesQuery(disputeId), http))
            .WithContract<IReadOnlyList<GateResultDto>>("getDisputeGates", "Gate trail: one result per executed gate, in order");
    }
}
