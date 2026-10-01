using Chargeback.Api.Common.Http;
using Chargeback.SharedKernel.Results;
using MediatR;

namespace Chargeback.Api.Common.Results;

/// <summary>Maps <see cref="Result"/> to HTTP. All failures are RFC 9457 problem details with a stable <c>code</c>.</summary>
public static class ResultHttpMapper
{
    public static int StatusCodeFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.NotImplemented => StatusCodes.Status501NotImplemented,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        ErrorType.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        ErrorType.PreconditionRequired => StatusCodes.Status428PreconditionRequired,
        ErrorType.Unprocessable => StatusCodes.Status422UnprocessableEntity,
        ErrorType.Failure => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>ProblemDetails extension carrying the request correlation id (approved contract, common guide §3.4).</summary>
    public const string TraceIdField = "traceId";

    public static IResult ToProblem(Error error, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(httpContext);

        var extensions = new Dictionary<string, object?>
        {
            ["code"] = error.Code,
            [TraceIdField] = CorrelationId.Get(httpContext),
        };

        if (error.Type == ErrorType.Validation && error.ValidationErrors is { } errors)
        {
            return TypedResults.ValidationProblem(
                errors, detail: error.Message, title: "Validation failed", extensions: extensions);
        }

        return TypedResults.Problem(
            detail: error.Message,
            statusCode: StatusCodeFor(error.Type),
            title: TitleFor(error.Type),
            extensions: extensions);
    }

    private static string TitleFor(ErrorType type) => type switch
    {
        ErrorType.Unauthorized => "Unauthorized",
        ErrorType.Forbidden => "Forbidden",
        ErrorType.NotFound => "Not found",
        ErrorType.Conflict => "Conflict",
        ErrorType.NotImplemented => "Not implemented",
        ErrorType.Unavailable => "Service unavailable",
        ErrorType.PreconditionFailed => "Precondition failed",
        ErrorType.PreconditionRequired => "Precondition required",
        ErrorType.Unprocessable => "Unprocessable request",
        _ => "Request failed",
    };
}

/// <summary>Endpoint helpers: send through MediatR and translate the Result.</summary>
public static class Dispatch
{
    public static async Task<IResult> Send<T>(
        ISender sender, IRequest<Result<T>> request, HttpContext httpContext, Func<T, IResult>? onSuccess = null)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(httpContext);

        var result = await sender.Send(request, httpContext.RequestAborted);
        return result.IsSuccess
            ? onSuccess?.Invoke(result.Value) ?? TypedResults.Ok(result.Value)
            : ResultHttpMapper.ToProblem(result.Error, httpContext);
    }

    public static async Task<IResult> Send(ISender sender, IRequest<Result> request, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(httpContext);

        var result = await sender.Send(request, httpContext.RequestAborted);
        return result.IsSuccess ? TypedResults.NoContent() : ResultHttpMapper.ToProblem(result.Error, httpContext);
    }
}
