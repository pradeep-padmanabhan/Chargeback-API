using System.Net;
using System.Net.Http.Json;
using System.Text;
using Chargeback.Api.Common.Http;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.IntegrationTests.Infrastructure;
using Chargeback.IntegrationTests.Security;
using Dapper;
using Npgsql;

namespace Chargeback.IntegrationTests.Intake;

/// <summary>Tests for the approved Phase 5 decisions (ADR-0106, ADR-0111, ADR-0117).</summary>
[Collection(PostgresCollection.Name)]
public sealed class Phase5DecisionTests(PostgresFixture fixture)
{
    /// <summary>
    /// ADR-0106 (implemented, 2026-09-29): formerly the KNOWN_LIMITATION test that documented duplicate disputes on
    /// retry. Inverted after the integration suite passed three consecutive runs: a retry with the same
    /// Idempotency-Key now replays the stored response and creates no second dispute.
    /// </summary>
    [Fact]
    public async Task ADR0106_same_idempotency_key_replays_and_creates_no_duplicate_dispute()
    {
        var bankId = await fixture.Data.CreateBankAsync();
        var user = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId, Permissions.CreateDispute);
        using var client = fixture.Factory.CreateClientFor(user.Sub);
        client.DefaultRequestHeaders.Add(IdempotencyKey.HeaderName, "same-key-" + Guid.NewGuid().ToString("N"));
        var body = $$"""{"bankId":"{{bankId}}","merchantName":"Retry Shop"}""";

        var first = await client.PostAsync("/api/v1/intake/disputes", new StringContent(body, Encoding.UTF8, "application/json"));
        var retry = await client.PostAsync("/api/v1/intake/disputes", new StringContent(body, Encoding.UTF8, "application/json"));

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        retry.StatusCode.Should().Be(HttpStatusCode.Accepted);
        retry.Headers.GetValues(IdempotencyKey.ReplayedHeaderName).Should().Equal("true");
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.disputes WHERE bank_id = @bankId", new { bankId }))
            .Should().Be(1, "the retry is replayed from the idempotency store (ADR-0106)");
    }

    [Theory]
    [InlineData("Store ref 1234567890123", HttpStatusCode.Accepted)]       // fails Luhn: accepted
    [InlineData("Loyalty 9876543210987654", HttpStatusCode.Accepted)]      // fails Luhn: accepted
    [InlineData("Shop 4111111111111111", HttpStatusCode.BadRequest)]       // Visa test PAN: rejected
    [InlineData("Shop 5500 0055 5555 5559", HttpStatusCode.BadRequest)]    // Mastercard test PAN: rejected
    public async Task Regression_long_digit_references_are_accepted_unless_luhn_valid(string merchantName, HttpStatusCode expected)
    {
        var bankId = await fixture.Data.CreateBankAsync();
        var user = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId, Permissions.CreateDispute);
        using var client = fixture.Factory.CreateClientFor(user.Sub);
        client.DefaultRequestHeaders.Add(IdempotencyKey.HeaderName, "idem-" + Guid.NewGuid().ToString("N"));

        var response = await client.PostAsync(
            "/api/v1/intake/disputes",
            new StringContent($$"""{"bankId":"{{bankId}}","merchantName":"{{merchantName}}"}""", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        if (expected == HttpStatusCode.Accepted)
        {
            (await fixture.Data.QueryScalarAsync<string>("SELECT merchant_name FROM chargeback_diagram.disputes WHERE bank_id = @bankId", new { bankId }))
                .Should().Be(merchantName, "a non-card reference is stored as entered");
        }
    }

    [Fact]
    public async Task Gate_trail_reports_execution_status_for_every_gate()
    {
        var bankId = await fixture.Data.CreateBankAsync();
        var bankUser = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId, Permissions.CreateDispute);
        var analyst = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases);
        await fixture.Data.GrantScopeAsync(analyst.Id, bankId);
        using var bankClient = fixture.Factory.CreateClientFor(bankUser.Sub);
        bankClient.DefaultRequestHeaders.Add(IdempotencyKey.HeaderName, "idem-" + Guid.NewGuid().ToString("N"));
        var response = await bankClient.PostAsync("/api/v1/intake/disputes", new StringContent($$"""{"bankId":"{{bankId}}"}""", Encoding.UTF8, "application/json"));
        var disputeId = (await response.Content.ReadFromJsonAsync<DisputeAcceptedResponse>(CurrentUserTests.Json))!.DisputeId;

        using var analystClient = fixture.Factory.CreateClientFor(analyst.Sub);
        var gates = (await analystClient.GetFromJsonAsync<List<GateResultDto>>($"/api/v1/disputes/{disputeId}/gate-results", CurrentUserTests.Json))!;

        gates.Should().HaveCount(10).And.OnlyContain(g => g.ExecutionStatus == GateExecutionStatus.PendingDefinition && g.Passed == null);
    }

    [Fact]
    public async Task Create_dispute_permission_is_seeded_but_assigned_to_no_role()
    {
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.permissions WHERE name = 'CREATE_DISPUTE' AND is_active"))
            .Should().Be(1);

        // Only roles created by tests reference it; the baseline assigns it to nobody.
        (await fixture.Data.QueryScalarAsync<long>(
            """
            SELECT count(*) FROM chargeback_diagram.role_permissions rp
            JOIN chargeback_diagram.permissions p ON p.id = rp.permission_id
            JOIN chargeback_diagram.roles r ON r.id = rp.role_id
            WHERE p.name = 'CREATE_DISPUTE' AND r.name NOT LIKE 'role-%'
            """)).Should().Be(0);
    }

    [Fact]
    public async Task Migration_0002_upgrades_an_original_baseline_database_idempotently()
    {
        var database = "mig_" + Guid.NewGuid().ToString("N")[..10];
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.ExecuteAsync($"CREATE DATABASE {database}");
        }

        var target = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        await BaselineDatabase.ApplyAsync(target);
        await using var connection = new NpgsqlConnection(target);

        // Simulate a development database created from the ORIGINAL baseline (before ADR-0111).
        await connection.ExecuteAsync("DELETE FROM chargeback_diagram.permissions WHERE name = 'CREATE_DISPUTE'");

        var migration = BaselineDatabase.MigrationPath("0002_add_create_dispute_permission.sql");
        await BaselineDatabase.ExecuteScriptAsync(target, migration);
        await BaselineDatabase.ExecuteScriptAsync(target, migration); // idempotent re-run

        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.permissions WHERE name = 'CREATE_DISPUTE'"))
            .Should().Be(1);
        (await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM chargeback_diagram.role_permissions rp JOIN chargeback_diagram.permissions p ON p.id = rp.permission_id WHERE p.name = 'CREATE_DISPUTE'"))
            .Should().Be(0, "the migration defines the permission only; role assignment needs approval");
        (await connection.QueryAsync<string>("SELECT name FROM chargeback_diagram.permissions ORDER BY name"))
            .Should().BeEquivalentTo(Permissions.Seeded, "a migrated database matches a fresh install");
    }
}
