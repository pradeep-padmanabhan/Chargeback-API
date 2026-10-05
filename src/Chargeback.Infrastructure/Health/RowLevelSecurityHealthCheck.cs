using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Chargeback.Infrastructure.Health;

/// <summary>
/// <c>RowLevelSecurity:RequireEnforcedLogin</c>: when true (the default), readiness fails unless the API's database login
/// is actually subject to row-level security. Development and superuser-based test hosts set it to false.
/// </summary>
public sealed class RowLevelSecurityOptions
{
    public const string SectionName = "RowLevelSecurity";

    /// <summary>Tables protected by migration 0010 (ADR-0006). Raised when a migration protects more.</summary>
    public const int ProtectedTableCount = 12;

    public bool RequireEnforcedLogin { get; set; } = true;
}

/// <summary>
/// Readiness guard for guide §8 #41: a superuser or BYPASSRLS login silently skips every RLS policy, so the API refuses
/// to report ready when its login could see every bank's rows, is not a member of <c>chargeback_app</c>, or the
/// policies are not installed. Read-only probe.
/// </summary>
internal sealed class RowLevelSecurityHealthCheck(NpgsqlDataSource dataSource, IOptions<RowLevelSecurityOptions> options) : IHealthCheck
{
    private const string Sql = """
        SELECT r.rolsuper AS is_superuser,
               r.rolbypassrls AS bypasses_rls,
               CASE WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'chargeback_app')
                    THEN pg_has_role(current_user, 'chargeback_app', 'MEMBER') ELSE false END AS is_app_member,
               (SELECT count(*) FROM pg_class c
                WHERE c.relnamespace = 'chargeback_diagram'::regnamespace AND c.relrowsecurity AND c.relforcerowsecurity) AS protected_tables
        FROM pg_roles r
        WHERE r.rolname = current_user
        """;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.Value.RequireEnforcedLogin)
        {
            return HealthCheckResult.Healthy("Row-level security login check disabled (RowLevelSecurity:RequireEnforcedLogin=false).");
        }

        try
        {
            await using var command = dataSource.CreateCommand(Sql);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("Current database login not found in pg_roles.");
            }

            var problems = new List<string>();
            if (reader.GetBoolean(0))
            {
                problems.Add("the API's database login is a superuser, so row-level security is skipped");
            }

            if (reader.GetBoolean(1))
            {
                problems.Add("the API's database login has BYPASSRLS, so row-level security is skipped");
            }

            if (!reader.GetBoolean(2))
            {
                problems.Add("the API's database login is not a member of chargeback_app");
            }

            var protectedTables = reader.GetInt64(3);
            if (protectedTables < RowLevelSecurityOptions.ProtectedTableCount)
            {
                problems.Add($"row-level security is forced on {protectedTables} of {RowLevelSecurityOptions.ProtectedTableCount} expected tables (migration 0010 missing?)");
            }

            return problems.Count == 0
                ? HealthCheckResult.Healthy("Row-level security enforced for the API's database login.")
                : HealthCheckResult.Unhealthy("Row-level security not enforced: " + string.Join("; ", problems) + " (guide §8 #41).");
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}
