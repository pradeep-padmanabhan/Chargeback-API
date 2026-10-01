using System.Text.Json;
using System.Text.Json.Nodes;
using Chargeback.Api.Common.Http;
using Chargeback.TestSupport;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Chargeback.ContractTests;

public sealed class ApiFixture : IDisposable
{
    public ChargebackApiFactory Factory { get; } = new(ChargebackApiFactory.UnusedConnectionString);

    public void Dispose() => Factory.Dispose();
}

/// <summary>
/// The OpenAPI document is the versioned contract for the frontend and AI teams.
/// Any change must be deliberate: regenerate with UPDATE_CONTRACT_SNAPSHOTS=true and commit
/// docs/contracts/openapi-v1.json together with the code change.
/// </summary>
public sealed class OpenApiContractTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static readonly string SnapshotPath = Path.Combine(RepositoryRoot(), "docs", "contracts", "openapi-v1.json");

    [Fact]
    public async Task OpenApi_document_matches_committed_contract()
    {
        using var client = fixture.Factory.CreateClientFor(null);
        var json = await client.GetStringAsync("/openapi/v1.json");
        var current = Normalize(json);

        if (string.Equals(Environment.GetEnvironmentVariable("UPDATE_CONTRACT_SNAPSHOTS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            await File.WriteAllTextAsync(SnapshotPath, current);
        }

        File.Exists(SnapshotPath).Should().BeTrue("the contract snapshot must be committed (run with UPDATE_CONTRACT_SNAPSHOTS=true)");
        var committed = Normalize(await File.ReadAllTextAsync(SnapshotPath));
        current.Should().Be(committed, "the API contract changed; regenerate docs/contracts/openapi-v1.json deliberately");
    }

    [Fact]
    public async Task Every_operation_has_an_id_tag_and_auth_problems()
    {
        using var client = fixture.Factory.CreateClientFor(null);
        var document = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json"))!;
        var operations = document["paths"]!.AsObject()
            .SelectMany(path => path.Value!.AsObject().Select(op => (Path: path.Key, Method: op.Key, Op: op.Value!)))
            .ToList();

        operations.Should().HaveCountGreaterThan(40);
        foreach (var (path, method, op) in operations)
        {
            op["operationId"]?.GetValue<string>().Should().NotBeNullOrEmpty($"{method} {path} needs an operationId");
            op["tags"]!.AsArray().Should().NotBeEmpty($"{method} {path} needs a tag");
            if (!path.Contains("/webhooks/", StringComparison.Ordinal))
            {
                op["responses"]!.AsObject().Select(r => r.Key).Should().Contain(["401", "403"], $"{method} {path} must document auth failures");
            }
        }
    }

    [Theory]
    [InlineData("/api/v1/intake/disputes", "post")]
    [InlineData("/api/v1/intake/bulk", "post")]
    [InlineData("/api/v1/filings/{filingId}/confirmation", "post")]
    public async Task Idempotent_operations_require_the_header(string path, string method)
    {
        using var client = fixture.Factory.CreateClientFor(null);
        var document = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json"))!;

        var parameters = document["paths"]![path]![method]!["parameters"]!.AsArray();
        parameters.Should().Contain(p => p!["name"]!.GetValue<string>() == IdempotencyKey.HeaderName && p["required"]!.GetValue<bool>());
    }

    [Fact]
    public void Only_health_openapi_and_signed_webhooks_allow_anonymous_access()
    {
        var endpoints = fixture.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

        var anonymous = endpoints
            .Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText)
            .ToArray();

        anonymous.Should().OnlyContain(p =>
            p!.StartsWith("/health/", StringComparison.Ordinal)
            || p.StartsWith("/openapi/", StringComparison.Ordinal)
            || p.StartsWith("/scalar", StringComparison.Ordinal)
            || p == "/api/v1/webhooks/zendesk");
    }

    /// <summary>Key-sorted, indented, LF: diffs show real contract changes, not registration order.</summary>
    private static string Normalize(string json) =>
        Sort(JsonNode.Parse(json))!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sort(p.Value?.DeepClone())))),
        JsonArray array => new JsonArray(array.Select(item => Sort(item?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Chargeback.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Chargeback.sln) not found.");
    }
}
