using System.Text.RegularExpressions;
using Chargeback.Infrastructure.Correlation;
using Serilog.Context;

namespace Chargeback.Api.Common.Http;

public static partial class CorrelationId
{
    public const string HeaderName = "X-Correlation-Id";
    private const string ItemKey = "chargeback.correlationId";

    public static string Get(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return httpContext.Items.TryGetValue(ItemKey, out var value) && value is string id
            ? id
            : httpContext.TraceIdentifier;
    }

    /// <summary>Accepts a caller-supplied id only if it is short and safe to log; otherwise generates one.</summary>
    public static string Normalize(string? candidate) =>
        candidate is not null && SafeId().IsMatch(candidate) ? candidate : Guid.NewGuid().ToString("N");

    internal static void Set(HttpContext httpContext, string id) => httpContext.Items[ItemKey] = id;

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeId();
}

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext httpContext)
    {
        var id = CorrelationId.Normalize(httpContext.Request.Headers[CorrelationId.HeaderName].FirstOrDefault());
        CorrelationId.Set(httpContext, id);
        httpContext.Response.OnStarting(() =>
        {
            httpContext.Response.Headers[CorrelationId.HeaderName] = id;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", id))
        {
            await next(httpContext);
        }
    }
}

internal sealed class HttpCorrelationIdProvider(IHttpContextAccessor accessor) : ICorrelationIdProvider
{
    private readonly string _fallback = Guid.NewGuid().ToString("N");

    public string CorrelationId => accessor.HttpContext is { } http ? global::Chargeback.Api.Common.Http.CorrelationId.Get(http) : _fallback;
}
