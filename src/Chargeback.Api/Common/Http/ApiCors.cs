namespace Chargeback.Api.Common.Http;

/// <summary>
/// CORS for the browser surfaces (Analyst Portal, Client Portal). Origins come from <c>Cors:AllowedOrigins</c>; the list
/// is empty by default (no cross-origin access) because the hosting domains per surface are not approved yet
/// (common guide §8 #20). Bearer tokens only, so credentials (cookies) are not allowed.
/// </summary>
public static class ApiCors
{
    public const string PolicyName = "ChargebackBrowserSurfaces";
    public const string AllowedOriginsKey = "Cors:AllowedOrigins";

    /// <summary>
    /// Response headers the frontend must read: <c>Retry-After</c> (409 IDEMPOTENCY_REQUEST_IN_PROGRESS, ADR-0106),
    /// <c>Idempotent-Replayed</c> and <c>ETag</c>.
    /// </summary>
    public static readonly string[] ExposedHeaders = ["Retry-After", IdempotencyKey.ReplayedHeaderName, "ETag"];

    /// <summary>Request headers the frontend sends, including <c>If-Match</c> for <c>/assignment</c>.</summary>
    public static readonly string[] AllowedRequestHeaders =
        ["Authorization", "Content-Type", IdempotencyKey.HeaderName, "If-Match", CorrelationId.HeaderName];

    public static IServiceCollection AddChargebackCors(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var origins = configuration.GetSection(AllowedOriginsKey).Get<string[]>() ?? [];
        return services.AddCors(options => options.AddPolicy(PolicyName, policy => policy
            .WithOrigins(origins)
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
            .WithHeaders(AllowedRequestHeaders)
            .WithExposedHeaders(ExposedHeaders)
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
    }
}
