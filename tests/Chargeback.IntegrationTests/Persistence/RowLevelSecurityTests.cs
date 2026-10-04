using Chargeback.IntegrationTests.Infrastructure;
using Dapper;
using Npgsql;

namespace Chargeback.IntegrationTests.Persistence;

/// <summary>
/// Migration 0010 (ADR-0006) at the database: a login in chargeback_app sees only the banks in <c>app.bank_ids</c>,
/// nothing when the scope is unset, everything in system scope; chargeback_migrations bypasses RLS. Superusers (the test
/// fixture's own login) are not subject to RLS, so these tests connect as a dedicated non-superuser login.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RowLevelSecurityTests(PostgresFixture fixture)
{
    private static readonly string[] Protected =
    [
        "disputes", "cases", "gate_results", "triage_results", "document_slots", "documents", "document_classifications",
        "case_review_decisions", "portal_messages", "zendesk_tickets", "mastercom_filings", "filing_api_log",
    ];

    [Fact]
    public async Task Every_bank_owned_table_has_forced_rls_and_one_policy()
    {
        var rows = await fixture.Data.QueryScalarAsync<long>(
            """
            SELECT count(*) FROM pg_class c
            WHERE c.relnamespace = 'chargeback_diagram'::regnamespace AND c.relrowsecurity AND c.relforcerowsecurity
              AND c.relname = ANY(@names)
              AND EXISTS (SELECT 1 FROM pg_policies p WHERE p.schemaname = 'chargeback_diagram' AND p.tablename = c.relname AND p.policyname = 'bank_scope')
            """,
            new { names = Protected });

        rows.Should().Be(Protected.Length);
    }

    [Fact]
    public async Task App_login_sees_only_its_banks_and_nothing_without_a_scope()
    {
        var a = await SeedBankAsync();
        var b = await SeedBankAsync();
        await using var app = await AppConnectionAsync();

        // Unset scope: fail closed.
        (await VisibleAsync(app, a)).Should().OnlyContain(kv => kv.Value == 0, "no scope means no rows");

        await SetScopeAsync(app, "banks", a.BankId);
        (await VisibleAsync(app, a)).Should().OnlyContain(kv => kv.Value == 1, "bank A sees its own rows in every protected table");
        (await VisibleAsync(app, b)).Should().OnlyContain(kv => kv.Value == 0, "bank A never sees bank B");

        await SetScopeAsync(app, "banks", a.BankId, b.BankId);
        (await VisibleAsync(app, b)).Should().OnlyContain(kv => kv.Value == 1, "a processor scoped to both banks sees both");

        await SetScopeAsync(app, "system");
        (await VisibleAsync(app, b)).Should().OnlyContain(kv => kv.Value == 1, "workflow steps run in system scope");
    }

    [Fact]
    public async Task App_login_cannot_write_rows_of_another_bank()
    {
        var a = await SeedBankAsync();
        var b = await SeedBankAsync();
        await using var app = await AppConnectionAsync();
        await SetScopeAsync(app, "banks", a.BankId);

        var insertDispute = () => app.ExecuteAsync(
            "INSERT INTO chargeback_diagram.disputes(bank_id, intake_channel) VALUES (@bank, 'PORTAL')", new { bank = b.BankId });
        var insertMessage = () => app.ExecuteAsync(
            "INSERT INTO chargeback_diagram.portal_messages(case_id, sender_type, message_text) VALUES (@caseId, 'PROCESSOR', 'x')", new { b.CaseId });
        var update = await app.ExecuteAsync("UPDATE chargeback_diagram.cases SET priority = 'HIGH' WHERE id = @caseId", new { b.CaseId });

        (await insertDispute.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
        (await insertMessage.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
        update.Should().Be(0, "bank B's case is invisible, so nothing is updated");
    }

    [Fact]
    public async Task Migrations_role_bypasses_rls()
    {
        var b = await SeedBankAsync();
        await using var app = await AppConnectionAsync();
        await app.ExecuteAsync("SET ROLE chargeback_migrations");
        await SetScopeAsync(app, "");

        (await VisibleAsync(app, b)).Should().OnlyContain(kv => kv.Value == 1);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private sealed record SeededBank(Guid BankId, Guid DisputeId, Guid CaseId, Guid DocumentId, Guid FilingId);

    /// <summary>One row per protected table for a new bank (inserted by the superuser fixture login).</summary>
    private async Task<SeededBank> SeedBankAsync()
    {
        var bankId = await fixture.Data.CreateBankAsync();
        var reviewer = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null);
        await using var c = new NpgsqlConnection(fixture.ConnectionString);
        var disputeId = await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO chargeback_diagram.disputes(bank_id, intake_channel) VALUES (@bankId, 'PORTAL') RETURNING id", new { bankId });
        var caseId = await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO chargeback_diagram.cases(dispute_id, case_reference) VALUES (@disputeId, @reference) RETURNING id",
            new { disputeId, reference = "RLS-" + Guid.NewGuid().ToString("N")[..12] });
        await c.ExecuteAsync("INSERT INTO chargeback_diagram.gate_results(dispute_id, gate_number, gate_name) VALUES (@disputeId, 1, 'g')", new { disputeId });
        await c.ExecuteAsync("INSERT INTO chargeback_diagram.triage_results(case_id) VALUES (@caseId)", new { caseId });
        await c.ExecuteAsync("INSERT INTO chargeback_diagram.document_slots(case_id, slot_name) VALUES (@caseId, 's')", new { caseId });
        var documentId = await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO chargeback_diagram.documents(case_id, file_name, s3_key, scheme_stage) VALUES (@caseId, 'f.pdf', 'k', 'Initial') RETURNING id", new { caseId });
        await c.ExecuteAsync(
            "INSERT INTO chargeback_diagram.document_classifications(document_id, status, failure_reason) VALUES (@documentId, 'FAILED', 'AI_UNAVAILABLE')", new { documentId });
        await c.ExecuteAsync(
            "INSERT INTO chargeback_diagram.case_review_decisions(case_id, decision, rationale, reviewed_by) VALUES (@caseId, 'REJECTED', 'r', @reviewer)",
            new { caseId, reviewer = reviewer.Id });
        await c.ExecuteAsync("INSERT INTO chargeback_diagram.portal_messages(case_id, sender_type, message_text) VALUES (@caseId, 'BANK', 'm')", new { caseId });
        await c.ExecuteAsync(
            "INSERT INTO chargeback_diagram.zendesk_tickets(case_id, zendesk_ticket_id) VALUES (@caseId, @ticket)", new { caseId, ticket = "RLS-" + Guid.NewGuid() });
        var filingId = await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO chargeback_diagram.mastercom_filings(case_id, idempotency_key) VALUES (@caseId, @key) RETURNING id", new { caseId, key = "RLS-" + Guid.NewGuid() });
        await c.ExecuteAsync("INSERT INTO chargeback_diagram.filing_api_log(filing_id, direction) VALUES (@filingId, 'OUTBOUND')", new { filingId });
        return new SeededBank(bankId, disputeId, caseId, documentId, filingId);
    }

    /// <summary>Rows of the seeded bank visible on <paramref name="connection"/>, per protected table.</summary>
    private static async Task<Dictionary<string, long>> VisibleAsync(NpgsqlConnection connection, SeededBank bank)
    {
        var queries = new Dictionary<string, string>
        {
            ["disputes"] = "SELECT count(*) FROM chargeback_diagram.disputes WHERE id = @DisputeId",
            ["cases"] = "SELECT count(*) FROM chargeback_diagram.cases WHERE id = @CaseId",
            ["gate_results"] = "SELECT count(*) FROM chargeback_diagram.gate_results WHERE dispute_id = @DisputeId",
            ["triage_results"] = "SELECT count(*) FROM chargeback_diagram.triage_results WHERE case_id = @CaseId",
            ["document_slots"] = "SELECT count(*) FROM chargeback_diagram.document_slots WHERE case_id = @CaseId",
            ["documents"] = "SELECT count(*) FROM chargeback_diagram.documents WHERE id = @DocumentId",
            ["document_classifications"] = "SELECT count(*) FROM chargeback_diagram.document_classifications WHERE document_id = @DocumentId",
            ["case_review_decisions"] = "SELECT count(*) FROM chargeback_diagram.case_review_decisions WHERE case_id = @CaseId",
            ["portal_messages"] = "SELECT count(*) FROM chargeback_diagram.portal_messages WHERE case_id = @CaseId",
            ["zendesk_tickets"] = "SELECT count(*) FROM chargeback_diagram.zendesk_tickets WHERE case_id = @CaseId",
            ["mastercom_filings"] = "SELECT count(*) FROM chargeback_diagram.mastercom_filings WHERE id = @FilingId",
            ["filing_api_log"] = "SELECT count(*) FROM chargeback_diagram.filing_api_log WHERE filing_id = @FilingId",
        };
        queries.Keys.Should().BeEquivalentTo(Protected);

        var counts = new Dictionary<string, long>();
        foreach (var (table, sql) in queries)
        {
            counts[table] = await connection.ExecuteScalarAsync<long>(sql, bank);
        }

        return counts;
    }

    private static Task SetScopeAsync(NpgsqlConnection connection, string scope, params Guid[] bankIds) =>
        connection.ExecuteAsync(
            "SELECT set_config('app.scope', @scope, false), set_config('app.bank_ids', @ids, false)",
            new { scope, ids = bankIds.Length == 0 ? "" : "{" + string.Join(',', bankIds) + "}" });

    private async Task<NpgsqlConnection> AppConnectionAsync()
    {
        var connection = new NpgsqlConnection(await RlsLogin.EnsureAsync(fixture.ConnectionString));
        await connection.OpenAsync();
        return connection;
    }
}

/// <summary>A non-superuser login in chargeback_app (cluster-wide, created once per container).</summary>
public static class RlsLogin
{
    public const string Name = "chargeback_app_test";
    private const string Password = "chargeback_app_test_pw";

    /// <returns>The connection string for the login against the same database.</returns>
    public static async Task<string> EnsureAsync(string superuserConnectionString)
    {
        await using (var admin = new NpgsqlConnection(superuserConnectionString))
        {
            await admin.ExecuteAsync(
                $"""
                DO $$
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{Name}') THEN
                    CREATE ROLE {Name} LOGIN PASSWORD '{Password}' NOSUPERUSER NOBYPASSRLS IN ROLE chargeback_app;
                  END IF;
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'chargeback_migrations') THEN
                    GRANT chargeback_migrations TO {Name};
                  END IF;
                END $$;
                """);
        }

        return new NpgsqlConnectionStringBuilder(superuserConnectionString) { Username = Name, Password = Password }.ConnectionString;
    }
}
