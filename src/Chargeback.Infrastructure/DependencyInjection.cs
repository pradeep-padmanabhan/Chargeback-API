using Chargeback.Infrastructure.Ai;
using Chargeback.Infrastructure.Ai.Capabilities;
using Chargeback.Infrastructure.Correlation;
using Chargeback.Infrastructure.Health;
using Chargeback.Infrastructure.Maintenance;
using Chargeback.Infrastructure.Outbox;
using Chargeback.Infrastructure.Persistence;
using Chargeback.Infrastructure.Persistence.Interceptors;
using Chargeback.Infrastructure.Persistence.Queries;
using Chargeback.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Chargeback.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Chargeback";
    public const string ReadinessTag = "ready";

    public static IServiceCollection AddChargebackInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        DapperConfiguration.Configure();

        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ICorrelationIdProvider, AmbientCorrelationIdProvider>();

        // Persistence (no migrations — ADR-0004).
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());
        services.AddScoped<OutboxInterceptor>();
        services.AddScoped<AuditingInterceptor>();
        services.AddDbContext<ChargebackDbContext>((sp, options) => options
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
            .AddInterceptors(sp.GetRequiredService<OutboxInterceptor>(), sp.GetRequiredService<AuditingInterceptor>()));
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IDapperQueryService, DapperQueryService>();

        // Security lookups.
        services.AddScoped<IUserAccessStore, UserAccessStore>();
        services.AddScoped<IResourceBankResolver, ResourceBankResolver>();

        // Outbox.
        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(OutboxOptions.SectionName));
        if (string.Equals(configuration[$"{OutboxOptions.SectionName}:Transport"], OutboxTransports.InProcess, StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<IIntegrationEventPublisher, InProcessIntegrationEventPublisher>();
        }
        else
        {
            services.TryAddSingleton<IIntegrationEventPublisher, LoggingIntegrationEventPublisher>();
        }
        services.AddSingleton<OutboxDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());

        // AI: common client + fail-soft capability defaults.
        services.AddOptions<AiOptions>().Bind(configuration.GetSection(AiOptions.SectionName));
        services.TryAddSingleton<IBedrockTransport, NotConfiguredBedrockTransport>();
        services.AddSingleton<IAiDecisionLogWriter, AiDecisionLogWriter>();
        services.AddSingleton<IBedrockAiClient, BedrockAiClient>();
        services.AddSingleton<UnavailableAiCapabilities>();
        services.TryAddSingleton<IEmailParser>(sp => sp.GetRequiredService<UnavailableAiCapabilities>());
        services.TryAddSingleton<ISdkIntakeGuide>(sp => sp.GetRequiredService<UnavailableAiCapabilities>());
        services.TryAddSingleton<IAttachmentMatcher>(sp => sp.GetRequiredService<UnavailableAiCapabilities>());
        services.TryAddSingleton<IDocumentVerifier>(sp => sp.GetRequiredService<UnavailableAiCapabilities>());
        services.TryAddSingleton<ITriageSummarizer>(sp => sp.GetRequiredService<UnavailableAiCapabilities>());
        services.TryAddSingleton<IEvidenceAnalyzer>(sp => sp.GetRequiredService<UnavailableAiCapabilities>());

        // ADR-0106 nightly purge (idempotency keys + processed events, 90 days).
        services.AddOptions<IdempotencyOptions>().Bind(configuration.GetSection(IdempotencyOptions.SectionName));
        services.AddSingleton<IdempotencyKeyPurgeJob>();
        services.AddHostedService(sp => sp.GetRequiredService<IdempotencyKeyPurgeJob>());

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: [ReadinessTag])
            .AddCheck<IdempotencyPurgeHealthCheck>("idempotency-purge", HealthStatus.Degraded, tags: [ReadinessTag]);

        return services;
    }
}
