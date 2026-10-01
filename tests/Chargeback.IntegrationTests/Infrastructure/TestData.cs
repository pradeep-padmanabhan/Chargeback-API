using Chargeback.Api.Common.Security;
using Dapper;
using Npgsql;

namespace Chargeback.IntegrationTests.Infrastructure;

public sealed record TestUser(Guid Id, string Sub, Guid? BankId);

public sealed record BankWorld(Guid BankId, Guid DisputeId, Guid CaseId, Guid DocumentId, Guid FilingId, TestUser BankUser);

/// <summary>Direct SQL seeding into the ephemeral test database. Every call creates unique rows.</summary>
public sealed class TestData(string connectionString)
{
    public static readonly string[] AllPermissions = [.. Permissions.Seeded, .. Permissions.Proposed];

    /// <summary>
    /// Test-database only: inserts the PROPOSED permission names so the full RBAC matrix can be tested.
    /// Production seeding of these names awaits approval (ADR-0111).
    /// </summary>
    public async Task SeedProposedPermissionsAsync()
    {
        await using var c = await Open();
        foreach (var name in Permissions.Proposed)
        {
            await c.ExecuteAsync(
                "INSERT INTO chargeback_diagram.permissions(name, resource, action, description) VALUES (@name, 'TEST', 'TEST', 'test-only proposed permission') ON CONFLICT (name) DO NOTHING",
                new { name });
        }
    }

    public async Task<Guid> CreateBankAsync(string? status = null)
    {
        await using var c = await Open();
        return await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO chargeback_diagram.banks(bank_code, bank_name, status) VALUES (@code, @name, @status) RETURNING id",
            new { code = "B" + Unique(), name = "Test Bank", status = status ?? "ACTIVE" });
    }

    public async Task<Guid> CreateRoleAsync(string roleType, IEnumerable<string> permissions, bool active = true)
    {
        await using var c = await Open();
        var roleId = await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO chargeback_diagram.roles(name, role_type, is_active) VALUES (@name, @roleType, @active) RETURNING id",
            new { name = "role-" + Unique(), roleType, active });
        foreach (var permission in permissions)
        {
            await c.ExecuteAsync(
                """
                INSERT INTO chargeback_diagram.role_permissions(role_id, permission_id)
                SELECT @roleId, id FROM chargeback_diagram.permissions WHERE name = @permission
                """,
                new { roleId, permission });
        }

        return roleId;
    }

    public async Task<Guid> CreatePermissionAsync(string name, bool active)
    {
        await using var c = await Open();
        return await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO chargeback_diagram.permissions(name, resource, action, is_active) VALUES (@name, 'TEST', 'TEST', @active) RETURNING id",
            new { name, active });
    }

    public async Task<TestUser> CreateUserAsync(string userType, Guid roleId, Guid? bankId = null, string status = "ACTIVE")
    {
        var sub = "sub-" + Unique();
        await using var c = await Open();
        var id = await c.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO chargeback_diagram.users(bank_id, cognito_sub, email, full_name, user_type, role_id, status)
            VALUES (@bankId, @sub, @email, 'Test User', @userType, @roleId, @status) RETURNING id
            """,
            new { bankId, sub, email = sub + "@example.test", userType, roleId, status });
        return new TestUser(id, sub, bankId);
    }

    public async Task<TestUser> CreateUserWithPermissionsAsync(string userType, Guid? bankId, params string[] permissions) =>
        await CreateUserAsync(userType, await CreateRoleAsync(userType, permissions), bankId);

    public async Task GrantScopeAsync(Guid userId, Guid bankId, DateTimeOffset? validFrom = null, DateTimeOffset? validUntil = null)
    {
        await using var c = await Open();
        await c.ExecuteAsync(
            "INSERT INTO chargeback_diagram.user_bank_scopes(user_id, bank_id, valid_from, valid_until) VALUES (@userId, @bankId, @validFrom, @validUntil)",
            new { userId, bankId, validFrom = (validFrom ?? DateTimeOffset.UtcNow.AddMinutes(-5)).UtcDateTime, validUntil = validUntil?.UtcDateTime });
    }

    /// <summary>A bank with one dispute → case → document and filing, plus a bank user holding every permission.</summary>
    public async Task<BankWorld> CreateBankWorldAsync()
    {
        var bankId = await CreateBankAsync();
        await using var c = await Open();
        var disputeId = await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO chargeback_diagram.disputes(bank_id, card_number_masked, intake_channel) VALUES (@bankId, '************1111', 'PORTAL') RETURNING id",
            new { bankId });
        var caseId = await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO chargeback_diagram.cases(dispute_id, case_reference) VALUES (@disputeId, @reference) RETURNING id",
            new { disputeId, reference = "CASE-" + Unique() });
        var documentId = await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO chargeback_diagram.documents(case_id, file_name, s3_key, scheme_stage) VALUES (@caseId, 'evidence.pdf', @key, 'Initial') RETURNING id",
            new { caseId, key = "test/" + Unique() });
        var filingId = await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO chargeback_diagram.mastercom_filings(case_id, idempotency_key) VALUES (@caseId, @key) RETURNING id",
            new { caseId, key = "idem-" + Unique() });
        var bankUser = await CreateUserWithPermissionsAsync("BANK", bankId, AllPermissions);
        return new BankWorld(bankId, disputeId, caseId, documentId, filingId, bankUser);
    }

    public async Task<T> QueryScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var c = await Open();
        return await c.ExecuteScalarAsync<T>(sql, parameters) ?? throw new InvalidOperationException("No value.");
    }

    public async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await using var c = await Open();
        await c.ExecuteAsync(sql, parameters);
    }

    private async Task<NpgsqlConnection> Open()
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static string Unique() => Guid.NewGuid().ToString("N")[..12];
}
