using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Chargeback.TestSupport;

/// <summary>Hosts the real API in-process with the test authentication scheme.</summary>
public class ChargebackApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    /// <summary>Connection string that is never opened (for tests that do not touch the database).</summary>
    public const string UnusedConnectionString = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1";

    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>Extra configuration values (e.g. synthetic gate activation or scheme-rule settings in tests).</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    public HttpClient CreateClientFor(string? cognitoSub)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (cognitoSub is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, cognitoSub);
        }

        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Chargeback", connectionString);
        builder.UseSetting("Authentication:Cognito:Region", "eu-west-1");
        builder.UseSetting("Authentication:Cognito:UserPoolId", "eu-west-1_TESTPOOL");
        builder.UseSetting("Authentication:Cognito:AllowedClientIds:0", "test-client");
        // Diagnostics: CHARGEBACK_TEST_LOG_LEVEL=Error (etc.) surfaces server logs in test output.
        builder.UseSetting("Serilog:MinimumLevel:Default", Environment.GetEnvironmentVariable("CHARGEBACK_TEST_LOG_LEVEL") ?? "Fatal");
        builder.UseSetting("Idempotency:PurgeEnabled", "false");
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);

            ConfigureServices?.Invoke(services);
        });
    }
}
