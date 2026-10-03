using System.Net;
using Chargeback.Api.Common.Http;
using Chargeback.IntegrationTests.Infrastructure;
using Chargeback.TestSupport;

namespace Chargeback.IntegrationTests.Security;

/// <summary>CORS for browser surfaces: exposed headers the frontend reads (ADR-0106) and preflight for custom request headers.</summary>
[Collection(PostgresCollection.Name)]
public sealed class CorsTests(PostgresFixture fixture)
{
    private const string Origin = "https://analyst.example.test";

    [Fact]
    public async Task Allowed_origin_can_read_retry_after_idempotent_replayed_and_etag()
    {
        await using var factory = Factory(Origin);
        using var client = factory.CreateClientFor(null);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", Origin);

        var response = await client.SendAsync(request);

        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(Origin);
        var exposed = string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers"))
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        exposed.Should().Contain(["Retry-After", "Idempotent-Replayed", "ETag"]);
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse("bearer tokens only; no cookies");
    }

    [Fact]
    public async Task Preflight_allows_the_custom_request_headers_without_a_token()
    {
        await using var factory = Factory(Origin);
        using var client = factory.CreateClientFor(null);
        using var request = new HttpRequestMessage(HttpMethod.Options, $"/api/v1/cases/{Guid.NewGuid()}/transitions");
        request.Headers.Add("Origin", Origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,idempotency-key,if-match,x-correlation-id");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(Origin);
        var allowed = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")).ToUpperInvariant();
        allowed.Should().Contain("IDEMPOTENCY-KEY").And.Contain("IF-MATCH").And.Contain("AUTHORIZATION");
    }

    [Fact]
    public async Task Unlisted_origin_gets_no_cors_headers()
    {
        await using var factory = Factory(Origin);
        using var client = factory.CreateClientFor(null);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", "https://evil.example.test");

        var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task No_origins_are_allowed_by_default()
    {
        using var client = fixture.Factory.CreateClientFor(null);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", Origin);

        var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse("hosting domains are not approved yet (guide §8 #20)");
    }

    private ChargebackApiFactory Factory(string origin) => new(fixture.ConnectionString)
    {
        Settings = new Dictionary<string, string> { [ApiCors.AllowedOriginsKey + ":0"] = origin },
    };
}
