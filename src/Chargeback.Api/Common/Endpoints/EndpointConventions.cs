namespace Chargeback.Api.Common.Endpoints;

public static class EndpointConventions
{
    public const string ApiPrefix = "/api/v1";

    /// <summary>Standard contract metadata for an implemented endpoint.</summary>
    public static RouteHandlerBuilder WithContract<TResponse>(
        this RouteHandlerBuilder builder, string operationId, string summary, int successStatusCode = StatusCodes.Status200OK)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithName(operationId)
            .WithSummary(summary)
            .Produces<TResponse>(successStatusCode)
            .WithStandardProblems();
    }

    /// <summary>Contract metadata for an endpoint with no response body.</summary>
    public static RouteHandlerBuilder WithNoContentContract(this RouteHandlerBuilder builder, string operationId, string summary)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithName(operationId)
            .WithSummary(summary)
            .Produces(StatusCodes.Status204NoContent)
            .WithStandardProblems();
    }

    /// <summary>Marks a Phase 4 contract stub: documented and authorized, returns 501 until its phase ships.</summary>
    public static RouteHandlerBuilder AsStub(this RouteHandlerBuilder builder, string plannedPhase)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithDescription($"[STUB] Contract only; returns 501 once authorized. Planned: {plannedPhase}.")
            .WithTags("Stub")
            .ProducesProblem(StatusCodes.Status501NotImplemented);
    }

    private static RouteHandlerBuilder WithStandardProblems(this RouteHandlerBuilder builder) =>
        builder
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
}
