using System.Text.RegularExpressions;
using Chargeback.Api.Common.Idempotency;
using Chargeback.Api.Common.Results;

namespace Chargeback.Api.Common.Http;

/// <summary>Endpoint metadata: the operation requires an <c>Idempotency-Key</c> header (shown in OpenAPI).</summary>
public sealed class RequiresIdempotencyKeyMetadata;

public static partial class IdempotencyKey
{
    public const string HeaderName = "Idempotency-Key";

    /// <summary>Set on responses replayed from the idempotency store (ADR-0106).</summary>
    public const string ReplayedHeaderName = "Idempotent-Replayed";

    public static bool IsValid(string? value) => value is not null && SafeKey().IsMatch(value);

    public static string? From(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return httpContext.Request.Headers[HeaderName].FirstOrDefault();
    }

    /// <summary>
    /// Requires a well-formed Idempotency-Key header (400 otherwise). Replays are handled by
    /// <c>IdempotencyBehavior</c>; this filter adds the <c>Idempotent-Replayed: true</c> header to replayed responses.
    /// </summary>
    public static RouteHandlerBuilder RequireIdempotencyKey(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithMetadata(new RequiresIdempotencyKeyMetadata())
            .AddEndpointFilter(async (context, next) =>
            {
                if (!IsValid(From(context.HttpContext)))
                {
                    return ResultHttpMapper.ToProblem(Errors.IdempotencyKeyRequired, context.HttpContext);
                }

                var result = await next(context);
                if (context.HttpContext.RequestServices.GetService<IdempotencyContext>() is { Replayed: true })
                {
                    context.HttpContext.Response.Headers[ReplayedHeaderName] = "true";
                }

                return result;
            });
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]{8,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeKey();
}
