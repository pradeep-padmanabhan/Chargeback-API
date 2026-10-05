using Chargeback.Infrastructure.Ai.Capabilities;
using Chargeback.Infrastructure.Outbox;
using Chargeback.IntegrationTests.Triage;
using Chargeback.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Chargeback.IntegrationTests.Cases;

/// <summary>
/// Case Management suite: its own container, with outbox events delivered IN-PROCESS to the workflow consumers
/// (case creation, automatic triage). The background dispatcher stays disabled; tests drain the outbox
/// explicitly so every run is deterministic. Synthetic data rules as in <see cref="TriageFixture"/>.
/// </summary>
public sealed class CaseFixture : TriageFixture
{
    protected override IReadOnlyDictionary<string, string> ExtraSettings { get; } = new Dictionary<string, string>
    {
        ["Outbox:Transport"] = OutboxTransports.InProcess,
        ["Outbox:DispatcherEnabled"] = "false",
    };

    /// <summary>SYNTHETIC host only: the Triage Summary capability. The default host keeps the production (unavailable) one.</summary>
    public FakeTriageSummarizer Summarizer { get; } = new();

    /// <summary>SYNTHETIC host only: the Document Verification capability (always succeeds).</summary>
    public FakeDocumentVerifier Verifier { get; } = new();

    protected override void ConfigureSyntheticServices(IServiceCollection services)
    {
        Summarizer.ConnectionString = ConnectionString;
        services.AddSingleton<ITriageSummarizer>(Summarizer);
        services.AddSingleton<IDocumentVerifier>(Verifier);
    }

    /// <summary>Delivers every pending outbox event (including events raised by consumers) through <paramref name="host"/>.</summary>
    public static async Task DrainOutboxAsync(ChargebackApiFactory host)
    {
        var dispatcher = host.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(CancellationToken.None) > 0; i++)
        {
        }
    }
}

[CollectionDefinition(Name)]
public sealed class CaseCollection : ICollectionFixture<CaseFixture>
{
    public const string Name = "cases";
}
