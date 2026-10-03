using Chargeback.IntegrationTests.Infrastructure;
using Dapper;
using Npgsql;

namespace Chargeback.IntegrationTests.Persistence;

/// <summary>Migration 0005 (common guide v1.5 §3.1): the approved roles and role → permission matrix, idempotently.</summary>
[Collection(PostgresCollection.Name)]
public sealed class Migration0005Tests(PostgresFixture fixture)
{
    private const string Migration = "0005_role_permission_matrix.sql";

    private static readonly (string Role, string Type, string[] Permissions)[] Matrix =
    [
        ("Analyst", "PROCESSOR", ["VIEW_CASES", "UPDATE_CASE_STATUS", "VIEW_TRIAGE", "VIEW_BANK_USERS"]),
        ("Senior Analyst", "PROCESSOR", ["VIEW_CASES", "UPDATE_CASE_STATUS", "ASSIGN_CASE", "VIEW_TRIAGE", "RETRIAGE_CASE", "VIEW_BANK_USERS"]),
        ("Compliance Officer", "PROCESSOR", ["VIEW_CASES", "UPDATE_CASE_STATUS", "VIEW_TRIAGE", "VIEW_BANK_USERS"]),
        ("Admin", "ADMIN", ["VIEW_CASES", "UPDATE_CASE_STATUS", "ASSIGN_CASE", "VIEW_TRIAGE", "RETRIAGE_CASE", "VIEW_BANK_USERS"]),
    ];

    [Fact]
    public async Task Upgrades_a_pre_0005_database_and_is_idempotent()
    {
        var target = await NewDatabaseAsync();
        await BaselineDatabase.ApplyAsync(target);
        await using var connection = new NpgsqlConnection(target);

        // Simulate a database created before v1.5: no VIEW_TRIAGE / RETRIAGE_CASE definitions, then 0002-0004.
        await connection.ExecuteAsync("DELETE FROM chargeback_diagram.permissions WHERE name IN ('VIEW_TRIAGE', 'RETRIAGE_CASE')");
        foreach (var earlier in new[] { "0002_add_create_dispute_permission.sql", "0003_case_management.sql", "0004_idempotency_keys.sql" })
        {
            await BaselineDatabase.ExecuteScriptAsync(target, BaselineDatabase.MigrationPath(earlier));
        }

        await BaselineDatabase.ExecuteScriptAsync(target, BaselineDatabase.MigrationPath(Migration));
        await BaselineDatabase.ExecuteScriptAsync(target, BaselineDatabase.MigrationPath(Migration)); // idempotent re-run

        await AssertApprovedMatrixAsync(connection);
    }

    [Fact]
    public async Task Rerunning_on_a_complete_install_changes_nothing()
    {
        var target = await NewDatabaseAsync();
        await BaselineDatabase.ApplyAllAsync(target);

        await BaselineDatabase.ExecuteScriptAsync(target, BaselineDatabase.MigrationPath(Migration));
        await BaselineDatabase.ExecuteScriptAsync(target, BaselineDatabase.MigrationPath(Migration));

        await using var connection = new NpgsqlConnection(target);
        await AssertApprovedMatrixAsync(connection);
    }

    private static async Task AssertApprovedMatrixAsync(NpgsqlConnection connection)
    {
        (await connection.QueryAsync<(string Name, string RoleType)>("SELECT name, role_type FROM chargeback_diagram.roles ORDER BY name"))
            .Should().BeEquivalentTo(Matrix.Select(m => (m.Role, m.Type)), "exactly the four approved roles");

        var pairs = (await connection.QueryAsync<(string Role, string Permission)>(
            """
            SELECT r.name, p.name FROM chargeback_diagram.role_permissions rp
            JOIN chargeback_diagram.roles r ON r.id = rp.role_id
            JOIN chargeback_diagram.permissions p ON p.id = rp.permission_id
            """)).ToList();
        pairs.Should().HaveCount(20).And.OnlyHaveUniqueItems();
        pairs.Should().BeEquivalentTo(Matrix.SelectMany(m => m.Permissions.Select(p => (m.Role, p))));

        (await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM chargeback_diagram.permissions WHERE name IN ('VIEW_TRIAGE', 'RETRIAGE_CASE')")).Should().Be(2);
        (await connection.ExecuteScalarAsync<long>(
            """
            SELECT count(*) FROM chargeback_diagram.role_permissions rp
            JOIN chargeback_diagram.permissions p ON p.id = rp.permission_id WHERE p.name = 'CREATE_DISPUTE'
            """)).Should().Be(0, "CREATE_DISPUTE is outside the approved matrix (guide §8 #21)");
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.user_bank_scopes"))
            .Should().Be(0, "roles never grant bank scope");
    }

    private async Task<string> NewDatabaseAsync()
    {
        var database = "mig5_" + Guid.NewGuid().ToString("N")[..10];
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.ExecuteAsync($"CREATE DATABASE {database}");
        }

        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
    }
}
