using Chargeback.Api.Common.Messaging;
using Chargeback.Infrastructure.Outbox;
using Chargeback.SharedKernel.Results;
using Chargeback.TestSupport;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Chargeback.IntegrationTests.Infrastructure;

/// <summary>
/// Ephemeral PostgreSQL 16 container loaded with docs/CHARGEBACK_DIAGRAM_BASELINE.sql (approved Q2).
/// Nothing is shared or persisted; no EF migrations run. Requires Docker.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("chargeback_test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public ChargebackApiFactory Factory { get; private set; } = null!;

    public RecordingEventPublisher Publisher { get; } = new();

    public TestData Data { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await BaselineDatabase.ApplyAllAsync(ConnectionString);

        Data = new TestData(ConnectionString);
        await Data.SeedProposedPermissionsAsync();

        Factory = new ChargebackApiFactory(ConnectionString)
        {
            ConfigureServices = services =>
            {
                services.AddSingleton<IIntegrationEventPublisher>(Publisher);
                services.AddTransient<IRequestHandler<CreateTestBankCommand, Result<Guid>>, CreateTestBankHandler>();
            },
        };
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

public sealed class RecordingEventPublisher : IIntegrationEventPublisher
{
    private readonly List<(string EventType, string Json, string GroupId, string DedupId)> _published = [];

    public IReadOnlyList<(string EventType, string Json, string GroupId, string DedupId)> Published
    {
        get
        {
            lock (_published)
            {
                return _published.ToArray();
            }
        }
    }

    public Task PublishAsync(string eventType, string envelopeJson, string messageGroupId, string deduplicationId, CancellationToken cancellationToken)
    {
        lock (_published)
        {
            _published.Add((eventType, envelopeJson, messageGroupId, deduplicationId));
        }

        return Task.CompletedTask;
    }
}
