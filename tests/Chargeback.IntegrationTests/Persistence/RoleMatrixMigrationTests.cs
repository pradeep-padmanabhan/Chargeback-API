using Chargeback.IntegrationTests.Infrastructure;
using Dapper;
using Npgsql;

namespace Chargeback.IntegrationTests.Persistence;

/// <summary>The approved roles and matrix: 0005 (base), 0006 (REVIEW_CASE), 0007 (document permissions), 0008 (MANAGE_BANK_USERS, Bank User role).</summary>
[Collection(PostgresCollection.Name)]
public sealed class RoleMatrixMigrationTests(PostgresFixture fixture)
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

        // A complete install also has 0006 and 0007: REVIEW_CASE, UPLOAD_DOCUMENT and VIEW_DOCUMENTS for all four roles.
        await using var connection = new NpgsqlConnection(target);
        await AssertApprovedMatrixAsync(connection, CompleteInstallGrants);
    }

    [Fact]
    public async Task Migration_0006_grants_review_case_to_all_four_roles_and_is_idempotent()
    {
        var target = await NewDatabaseAsync();
        await BaselineDatabase.ApplyAllAsync(target);

        await BaselineDatabase.ExecuteScriptAsync(target, BaselineDatabase.MigrationPath("0006_human_review.sql"));
        await BaselineDatabase.ExecuteScriptAsync(target, BaselineDatabase.MigrationPath("0006_human_review.sql"));

        await using var connection = new NpgsqlConnection(target);
        await AssertApprovedMatrixAsync(connection, CompleteInstallGrants);
        (await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM pg_trigger WHERE tgname = 'case_review_decisions_append_only' AND NOT tgisinternal")).Should().Be(1);
    }

    [Fact]
    public async Task Migration_0007_grants_document_permissions_to_all_four_roles_and_is_idempotent()
    {
        var target = await NewDatabaseAsync();
        await BaselineDatabase.ApplyAllAsync(target);

        await BaselineDatabase.ExecuteScriptAsync(target, BaselineDatabase.MigrationPath("0007_evidence_documents.sql"));
        await BaselineDatabase.ExecuteScriptAsync(target, BaselineDatabase.MigrationPath("0007_evidence_documents.sql"));

        await using var connection = new NpgsqlConnection(target);
        await AssertApprovedMatrixAsync(connection, CompleteInstallGrants);
        (await connection.ExecuteScalarAsync<long>(
            """
            SELECT count(*) FROM pg_trigger
            WHERE tgname IN ('documents_upload_stage_immutable', 'document_classifications_append_only') AND NOT tgisinternal
            """)).Should().Be(2);
    }

    [Fact]
    public async Task Migration_0008_adds_manage_bank_users_and_the_bank_user_role_and_is_idempotent()
    {
        var target = await NewDatabaseAsync();
        await BaselineDatabase.ApplyAllAsync(target);

        await BaselineDatabase.ExecuteScriptAsync(target, BaselineDatabase.MigrationPath("0008_admin_user_management.sql"));
        await BaselineDatabase.ExecuteScriptAsync(target, BaselineDatabase.MigrationPath("0008_admin_user_management.sql"));

        await using var connection = new NpgsqlConnection(target);
        await AssertApprovedMatrixAsync(connection, CompleteInstallGrants);
        (await connection.ExecuteScalarAsync<long>(
            """
            SELECT count(*) FROM chargeback_diagram.role_permissions rp JOIN chargeback_diagram.roles r ON r.id = rp.role_id
            WHERE r.name = 'Bank User'
            """)).Should().Be(0, "bank users hold no permissions (guide §3.1)");
        (await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'users_deleted_disabled_check'")).Should().Be(1);
    }

    /// <summary>Granted to all four roles by migrations 0006 and 0007 (0008 grants MANAGE_BANK_USERS to Admin only).</summary>
    private static readonly string[] CompleteInstallGrants = ["REVIEW_CASE", "UPLOAD_DOCUMENT", "VIEW_DOCUMENTS"];

    private static async Task AssertApprovedMatrixAsync(NpgsqlConnection connection, string[]? grantedToAllRoles = null)
    {
        // A complete install (0008) also has the permissionless BANK-type "Bank User" role.
        var expectedRoles = Matrix.Select(m => (m.Role, m.Type)).ToList();
        if (grantedToAllRoles is not null)
        {
            expectedRoles.Add(("Bank User", "BANK"));
        }

        (await connection.QueryAsync<(string Name, string RoleType)>("SELECT name, role_type FROM chargeback_diagram.roles ORDER BY name"))
            .Should().BeEquivalentTo(expectedRoles, "exactly the approved roles");

        var pairs = (await connection.QueryAsync<(string Role, string Permission)>(
            """
            SELECT r.name, p.name FROM chargeback_diagram.role_permissions rp
            JOIN chargeback_diagram.roles r ON r.id = rp.role_id
            JOIN chargeback_diagram.permissions p ON p.id = rp.permission_id
            """)).ToList();
        var expected = Matrix.SelectMany(m => m.Permissions.Select(p => (m.Role, p))).ToList();
        expected.AddRange(Matrix.SelectMany(m => (grantedToAllRoles ?? []).Select(p => (m.Role, p))));
        if (grantedToAllRoles is not null)
        {
            expected.Add(("Admin", "MANAGE_BANK_USERS")); // migration 0008: Admin only (guide v1.8 §3.1)
        }

        pairs.Should().HaveCount(expected.Count).And.OnlyHaveUniqueItems();
        pairs.Should().BeEquivalentTo(expected);

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
