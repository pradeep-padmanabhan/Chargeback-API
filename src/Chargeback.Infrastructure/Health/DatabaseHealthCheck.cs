using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Chargeback.Infrastructure.Health;

/// <summary>Readiness: the database is reachable and the baseline schema exists (read-only probe).</summary>
internal sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand(
                "SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'chargeback_diagram'");
            var found = (long)(await command.ExecuteScalarAsync(cancellationToken))! == 1;
            return found
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Baseline schema chargeback_diagram not found.");
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}
