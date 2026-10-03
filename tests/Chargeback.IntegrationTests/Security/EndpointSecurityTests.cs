using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Chargeback.Api.Common.Http;
using Chargeback.IntegrationTests.Infrastructure;
using Chargeback.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Chargeback.IntegrationTests.Security;

/// <summary>
/// Discovers every mapped endpoint, so a new endpoint is covered automatically:
/// (1) no credentials → 401; (2) cross-bank access → never succeeds, reported as 404.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed partial class EndpointSecurityTests(PostgresFixture fixture)
{
    private static readonly string[] ScopedRouteParameters = ["caseId", "disputeId", "documentId", "filingId", "bankId", "userId"];

    /// <summary>Scoped-looking parameters on [NotBankScoped] operations; the handler enforces granter scope (Phase 12).</summary>
    private static readonly string[] IsolationExclusions = ["/bank-scopes"];

    [Fact]
    public async Task Every_protected_endpoint_rejects_missing_credentials()
    {
        using var client = fixture.Factory.CreateClientFor(null);
        var checkedCount = 0;

        foreach (var endpoint in ApiEndpoints().Where(e => !e.AllowsAnonymous))
        {
            var response = await Send(client, endpoint, _ => Guid.NewGuid());
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{endpoint.Method} {endpoint.Pattern}");
            checkedCount++;
        }

        checkedCount.Should().BeGreaterThan(40);
    }

    [Fact]
    public async Task Sdk_endpoints_reject_portal_credentials_until_sdk_auth_is_approved()
    {
        var world = await fixture.Data.CreateBankWorldAsync();
        using var client = fixture.Factory.CreateClientFor(world.BankUser.Sub);

        foreach (var endpoint in ApiEndpoints().Where(e => e.Pattern.StartsWith("/api/v1/sdk/", StringComparison.Ordinal)))
        {
            (await Send(client, endpoint, _ => Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.Unauthorized, endpoint.Pattern);
        }
    }

    [Fact]
    public async Task Cross_bank_access_is_never_granted()
    {
        var own = await fixture.Data.CreateBankWorldAsync();
        var foreign = await fixture.Data.CreateBankWorldAsync();
        var processor = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, TestData.AllPermissions);
        await fixture.Data.GrantScopeAsync(processor.Id, own.BankId);
        var callers = new[] { processor.Sub, own.BankUser.Sub };

        var scopedEndpoints = ApiEndpoints()
            .Where(e => !e.AllowsAnonymous
                && !e.Pattern.StartsWith("/api/v1/sdk/", StringComparison.Ordinal)
                && e.Parameters.Any(p => ScopedRouteParameters.Contains(p))
                && !IsolationExclusions.Any(x => e.Pattern.Contains(x, StringComparison.Ordinal)))
            .ToList();
        scopedEndpoints.Should().HaveCountGreaterThan(25);

        foreach (var endpoint in scopedEndpoints)
        {
            var foreignStatuses = new List<HttpStatusCode>();
            var ownStatuses = new List<HttpStatusCode>();
            foreach (var sub in callers)
            {
                using var client = fixture.Factory.CreateClientFor(sub);
                foreignStatuses.Add((await Send(client, endpoint, p => IdFor(foreign, p))).StatusCode);
                ownStatuses.Add((await Send(client, endpoint, p => IdFor(own, p))).StatusCode);
            }

            var label = $"{endpoint.Method} {endpoint.Pattern} foreign=[{string.Join(",", foreignStatuses)}] own=[{string.Join(",", ownStatuses)}]";

            // Another bank's resource: only 403 (user type/permission, which reveals nothing) or 404.
            foreignStatuses.Should().OnlyContain(s => s == HttpStatusCode.NotFound || s == HttpStatusCode.Forbidden, label);
            foreignStatuses.Should().Contain(HttpStatusCode.NotFound, label);

            // Positive control: for the same caller type, the caller's own resource passes the scope check.
            ownStatuses.Should().Contain(s => s != HttpStatusCode.NotFound && s != HttpStatusCode.Forbidden, label);
        }
    }

    [Fact]
    public async Task Bank_id_in_body_is_scope_checked()
    {
        var own = await fixture.Data.CreateBankWorldAsync();
        var foreign = await fixture.Data.CreateBankAsync();
        using var client = fixture.Factory.CreateClientFor(own.BankUser.Sub);
        client.DefaultRequestHeaders.Add(IdempotencyKey.HeaderName, "idem-" + Guid.NewGuid().ToString("N"));

        var foreignResponse = await client.PostAsync("/api/v1/intake/disputes", Json($$"""{"bankId":"{{foreign}}"}"""));
        var ownResponse = await client.PostAsync("/api/v1/intake/disputes", Json($$"""{"bankId":"{{own.BankId}}"}"""));

        foreignResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ownResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Idempotency_key_is_required_where_declared()
    {
        var own = await fixture.Data.CreateBankWorldAsync();
        using var client = fixture.Factory.CreateClientFor(own.BankUser.Sub);

        var response = await client.PostAsync("/api/v1/intake/disputes", Json($$"""{"bankId":"{{own.BankId}}"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("IDEMPOTENCY_KEY_REQUIRED");
    }

    [Fact]
    public async Task Correlation_id_is_echoed()
    {
        using var client = fixture.Factory.CreateClientFor(null);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add(CorrelationId.HeaderName, "trace-abc-123");

        var response = await client.SendAsync(request);

        response.Headers.GetValues(CorrelationId.HeaderName).Should().Equal("trace-abc-123");
    }

    [Fact]
    public async Task Health_endpoints_report_database_readiness()
    {
        using var client = fixture.Factory.CreateClientFor(null);

        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static Guid IdFor(BankWorld world, string parameter) => parameter switch
    {
        "caseId" => world.CaseId,
        "disputeId" => world.DisputeId,
        "documentId" => world.DocumentId,
        "filingId" => world.FilingId,
        "bankId" => world.BankId,
        "userId" => world.BankUser.Id,
        _ => Guid.NewGuid(),
    };

    private IEnumerable<ApiEndpoint> ApiEndpoints() =>
        fixture.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => new ApiEndpoint(
                m,
                e.RoutePattern.RawText!,
                e.RoutePattern.Parameters.Select(p => p.Name).ToArray(),
                e.Metadata.GetMetadata<IAllowAnonymous>() is not null)));

    private static async Task<HttpResponseMessage> Send(HttpClient client, ApiEndpoint endpoint, Func<string, Guid> idFor)
    {
        var url = RouteParameter().Replace(endpoint.Pattern, m => idFor(m.Groups[1].Value).ToString());
        using var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), url);
        request.Headers.Add(IdempotencyKey.HeaderName, "idem-" + Guid.NewGuid().ToString("N"));

        // A stale version (here and expectedVersion 0 in /transitions bodies): requests that pass the scope check are
        // then refused with 412 or 409, never applied.
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        if (endpoint.Method is "POST" or "PUT" or "PATCH")
        {
            request.Content = endpoint.Pattern.EndsWith("/intake/bulk", StringComparison.Ordinal)
                ? new MultipartFormDataContent { { new StringContent(Guid.NewGuid().ToString()), "bankId" }, { new ByteArrayContent([1]), "file", "x.csv" } }
                : Json(WellFormedBodyFor(endpoint.Pattern));
        }

        return await client.SendAsync(request);
    }

    /// <summary>
    /// Implemented endpoints validate request shape before authorization (diagram order), so the suite sends
    /// well-formed bodies to make sure the bank-scope check is what it exercises.
    /// </summary>
    private static string WellFormedBodyFor(string pattern) => pattern switch
    {
        _ when pattern.EndsWith("/transitions", StringComparison.Ordinal) => """{"action":"START_REVIEW","rationale":"isolation test","expectedVersion":0}""",
        _ when pattern.EndsWith("/retriage", StringComparison.Ordinal) => """{"reason":"isolation test"}""",
        _ when pattern.EndsWith("/review/decision", StringComparison.Ordinal) => """{"decision":"Reject","rationale":"isolation test","expectedVersion":0}""",
        _ => "{}",
    };

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [GeneratedRegex(@"\{(\w+)(?::[^}]*)?\}")]
    private static partial Regex RouteParameter();

    private sealed record ApiEndpoint(string Method, string Pattern, string[] Parameters, bool AllowsAnonymous);
}
