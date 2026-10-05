using System.Net;
using System.Net.Http.Json;
using System.Text;
using Chargeback.Api.Common.Http;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Infrastructure.Maintenance;
using Chargeback.IntegrationTests.Infrastructure;
using Chargeback.IntegrationTests.Security;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Chargeback.IntegrationTests.Intake;

/// <summary>ADR-0106 (approved): API idempotency for submitDispute (atomic mode) and the nightly purge.</summary>
[Collection(PostgresCollection.Name)]
public sealed class IdempotencyTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Same_key_and_request_replays_without_creating_a_second_dispute()
    {
        var (bankId, sub) = await Submitter();
        var key = NewKey();
        var body = $$"""{"bankId":"{{bankId}}","merchantName":"Replay Shop","transactionAmount":12.50}""";

        var first = await Submit(sub, key, body);
        var retry = await Submit(sub, key, body);

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        retry.StatusCode.Should().Be(HttpStatusCode.Accepted);
        first.Headers.Contains(IdempotencyKey.ReplayedHeaderName).Should().BeFalse();
        retry.Headers.GetValues(IdempotencyKey.ReplayedHeaderName).Should().Equal("true");
        (await retry.Content.ReadAsStringAsync()).Should().Be(await first.Content.ReadAsStringAsync());
        retry.Headers.Location.Should().Be(first.Headers.Location);
        (await Disputes(bankId)).Should().Be(1);
        (await fixture.Data.QueryScalarAsync<long>(
            "SELECT count(*) FROM chargeback_diagram.idempotency_keys WHERE idempotency_key = @key AND state = 'COMPLETED' AND response_status = 202", new { key }))
            .Should().Be(1);
    }

    [Fact]
    public async Task Property_order_and_whitespace_do_not_change_the_request()
    {
        var (bankId, sub) = await Submitter();
        var key = NewKey();

        await Submit(sub, key, $$"""{"bankId":"{{bankId}}","merchantName":"Order Shop","transactionAmount":5}""");
        var retry = await Submit(sub, key, $$"""
            {  "transactionAmount" : 5.0,
               "merchantName": "Order Shop",   "bankId": "{{bankId}}" }
            """);

        retry.Headers.Contains(IdempotencyKey.ReplayedHeaderName).Should().BeTrue();
        (await Disputes(bankId)).Should().Be(1);
    }

    [Fact]
    public async Task Same_key_with_a_different_request_is_422_and_creates_nothing()
    {
        var (bankId, sub) = await Submitter();
        var key = NewKey();
        await Submit(sub, key, $$"""{"bankId":"{{bankId}}","merchantName":"First"}""");

        var reused = await Submit(sub, key, $$"""{"bankId":"{{bankId}}","merchantName":"Second"}""");

        reused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await reused.Content.ReadAsStringAsync()).Should().Contain("IDEMPOTENCY_KEY_REUSED");
        (await Disputes(bankId)).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_duplicates_create_exactly_one_dispute()
    {
        var (bankId, sub) = await Submitter();
        var key = NewKey();
        var body = $$"""{"bankId":"{{bankId}}","merchantName":"Race Shop"}""";

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Submit(sub, key, body)));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Accepted);
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        bodies.Distinct().Should().ContainSingle("every duplicate returns the winner's stored response");
        (await Disputes(bankId)).Should().Be(1);
    }

    [Fact]
    public async Task Keys_are_scoped_per_user()
    {
        var (bankId, first) = await Submitter();
        var second = (await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId, Permissions.CreateDispute)).Sub;
        var key = NewKey();
        var body = $$"""{"bankId":"{{bankId}}","merchantName":"Shared Key Shop"}""";

        await Submit(first, key, body);
        var other = await Submit(second, key, body);

        other.Headers.Contains(IdempotencyKey.ReplayedHeaderName).Should().BeFalse();
        (await Disputes(bankId)).Should().Be(2);
    }

    [Fact]
    public async Task Expired_key_is_treated_as_new()
    {
        var (bankId, sub) = await Submitter();
        var key = NewKey();
        var body = $$"""{"bankId":"{{bankId}}","merchantName":"Expiry Shop"}""";
        await Submit(sub, key, body);
        await fixture.Data.ExecuteAsync(
            "UPDATE chargeback_diagram.idempotency_keys SET expires_at = now() - interval '1 hour' WHERE idempotency_key = @key", new { key });

        var again = await Submit(sub, key, body);

        again.StatusCode.Should().Be(HttpStatusCode.Accepted);
        again.Headers.Contains(IdempotencyKey.ReplayedHeaderName).Should().BeFalse();
        (await Disputes(bankId)).Should().Be(2);
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.idempotency_keys WHERE idempotency_key = @key", new { key }))
            .Should().Be(1, "the expired row is replaced by the new result");
    }

    [Fact]
    public async Task Failed_requests_store_nothing_and_the_key_can_be_retried()
    {
        var (bankId, sub) = await Submitter();
        var key = NewKey();

        var invalid = await Submit(sub, key, $$"""{"bankId":"{{bankId}}","merchantName":"Shop 4111111111111111"}""");
        var forbidden = await Submit((await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId, Permissions.ViewCases)).Sub, key, $$"""{"bankId":"{{bankId}}"}""");
        var valid = await Submit(sub, key, $$"""{"bankId":"{{bankId}}","merchantName":"Fixed Shop"}""");

        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        valid.StatusCode.Should().Be(HttpStatusCode.Accepted);
        valid.Headers.Contains(IdempotencyKey.ReplayedHeaderName).Should().BeFalse();
        (await Disputes(bankId)).Should().Be(1);
    }

    [Fact]
    public async Task Stored_responses_contain_no_card_data()
    {
        var (bankId, sub) = await Submitter();
        var key = NewKey();
        await Submit(sub, key, $$"""{"bankId":"{{bankId}}","cardNumberMasked":"************4242","merchantName":"Card Shop"}""");

        var stored = await fixture.Data.QueryScalarAsync<string>("SELECT response_body::text FROM chargeback_diagram.idempotency_keys WHERE idempotency_key = @key", new { key });

        stored.Should().NotContain("4242").And.NotContain("card", "only the small response DTO is stored");
        stored.Should().Contain("disputeId");
    }

    [Fact]
    public async Task Purge_deletes_only_expired_keys_and_old_processed_events_in_batches()
    {
        var (bankId, _) = await Submitter();
        var principal = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId);
        var expired = Enumerable.Range(0, 5).Select(_ => NewKey()).ToArray();
        var fresh = NewKey();
        foreach (var k in expired)
        {
            await InsertKey(principal.Id, k, "now() - interval '1 minute'");
        }

        await InsertKey(principal.Id, fresh, "now() + interval '89 days'");
        var oldEvent = Guid.NewGuid();
        var recentEvent = Guid.NewGuid();
        await fixture.Data.ExecuteAsync(
            """
            INSERT INTO chargeback_diagram.processed_domain_events(event_id, consumer, processed_at)
            VALUES (@oldEvent, 'test', now() - interval '91 days'), (@recentEvent, 'test', now() - interval '89 days')
            """,
            new { oldEvent, recentEvent });

        var job = Job(batchSize: 2);
        var result = await job.RunOnceAsync(CancellationToken.None);

        result.Outcome.Should().Be(PurgeRunOutcome.Completed);
        result.IdempotencyKeysDeleted.Should().BeGreaterThanOrEqualTo(5, "five expired keys across three batches of two");
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.idempotency_keys WHERE idempotency_key = ANY(@expired)", new { expired }))
            .Should().Be(0);
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.idempotency_keys WHERE idempotency_key = @fresh", new { fresh }))
            .Should().Be(1);
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.processed_domain_events WHERE event_id = @oldEvent", new { oldEvent })).Should().Be(0);
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.processed_domain_events WHERE event_id = @recentEvent", new { recentEvent })).Should().Be(1);
    }

    [Fact]
    public async Task Purge_runs_on_only_one_instance_at_a_time()
    {
        var (bankId, _) = await Submitter();
        var principal = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId);
        var key = NewKey();
        await InsertKey(principal.Id, key, "now() - interval '1 minute'");

        await using (var otherInstance = new NpgsqlConnection(fixture.ConnectionString))
        {
            // Another ECS task mid-run: it holds the transaction-scoped lock.
            await otherInstance.OpenAsync();
            await using var running = await otherInstance.BeginTransactionAsync();
            await otherInstance.ExecuteAsync("SELECT pg_advisory_xact_lock(@k)", new { k = IdempotencyKeyPurgeJob.AdvisoryLockKey }, running);

            var skipped = await Job().RunOnceAsync(CancellationToken.None);

            skipped.Outcome.Should().Be(PurgeRunOutcome.SkippedLockHeld);
            (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.idempotency_keys WHERE idempotency_key = @key", new { key })).Should().Be(1);
            await running.RollbackAsync(); // the other run ends (even by failure): the lock is released
        }

        (await Job().RunOnceAsync(CancellationToken.None)).Outcome.Should().Be(PurgeRunOutcome.Completed);
    }

    [Fact]
    public async Task Health_degrades_when_purgeable_rows_are_overdue_for_more_than_48_hours()
    {
        var (bankId, _) = await Submitter();
        var principal = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId);
        await Job().RunOnceAsync(CancellationToken.None);
        (await Health()).Should().Be(HealthStatus.Healthy);

        await InsertKey(principal.Id, NewKey(), "now() - interval '49 hours'");
        (await Health()).Should().Be(HealthStatus.Degraded);

        await Job().RunOnceAsync(CancellationToken.None);
        (await Health()).Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Migration_0004_is_idempotent()
    {
        var database = "mig4_" + Guid.NewGuid().ToString("N")[..10];
        await fixture.Data.ExecuteAsync($"CREATE DATABASE {database}");
        var target = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        await BaselineDatabase.ApplyAsync(target);
        await BaselineDatabase.ExecuteScriptAsync(target, BaselineDatabase.MigrationPath("0003_case_management.sql"));
        var migration = BaselineDatabase.MigrationPath("0004_idempotency_keys.sql");

        await BaselineDatabase.ExecuteScriptAsync(target, migration);
        await BaselineDatabase.ExecuteScriptAsync(target, migration);

        var data = new TestData(target);
        (await data.QueryScalarAsync<long>("SELECT count(*) FROM pg_constraint WHERE conname = 'idempotency_keys_principal_operation_key'")).Should().Be(1);
        (await data.QueryScalarAsync<long>("SELECT count(*) FROM pg_indexes WHERE indexname IN ('ix_idempotency_keys_expires','ix_processed_domain_events_processed_at')")).Should().Be(2);
    }

    private IdempotencyKeyPurgeJob Job(int batchSize = 5_000) => new(
        fixture.Factory.Services.GetRequiredService<NpgsqlDataSource>(),
        Options.Create(new IdempotencyOptions { PurgeBatchSize = batchSize }),
        TimeProvider.System,
        NullLogger<IdempotencyKeyPurgeJob>.Instance);

    private async Task<HealthStatus> Health()
    {
        var check = new IdempotencyPurgeHealthCheck(
            fixture.Factory.Services.GetRequiredService<NpgsqlDataSource>(), Options.Create(new IdempotencyOptions()), TimeProvider.System);
        return (await check.CheckHealthAsync(new HealthCheckContext())).Status;
    }

    private Task InsertKey(Guid principalId, string key, string expiresSql) =>
        fixture.Data.ExecuteAsync(
            $$"""
            INSERT INTO chargeback_diagram.idempotency_keys(principal_id, operation, idempotency_key, request_hash, state, response_status, response_body, completed_at, expires_at)
            VALUES (@principalId, 'test', @key, repeat('0', 64), 'COMPLETED', 202, '{}'::jsonb, now(), {{expiresSql}})
            """,
            new { principalId, key });

    private async Task<(Guid BankId, string Sub)> Submitter()
    {
        var bankId = await fixture.Data.CreateBankAsync();
        var user = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId, Permissions.CreateDispute);
        return (bankId, user.Sub);
    }

    private async Task<HttpResponseMessage> Submit(string sub, string key, string body)
    {
        using var client = fixture.Factory.CreateClientFor(sub);
        client.DefaultRequestHeaders.Add(IdempotencyKey.HeaderName, key);
        return await client.PostAsync("/api/v1/intake/disputes", new StringContent(body, Encoding.UTF8, "application/json"));
    }

    private Task<long> Disputes(Guid bankId) =>
        fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.disputes WHERE bank_id = @bankId", new { bankId });

    private static string NewKey() => "idem-" + Guid.NewGuid().ToString("N");
}
