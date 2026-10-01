using System.Security.Claims;
using System.Text.Json;
using Chargeback.Infrastructure.Ai;
using Chargeback.Infrastructure.Outbox;
using Chargeback.IntegrationTests.Infrastructure;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Chargeback.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
public sealed class TransactionAndOutboxTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Successful_command_commits_state_and_outbox_event_atomically()
    {
        var code = "TX" + Guid.NewGuid().ToString("N")[..10];

        var result = await SendAsync(new CreateTestBankCommand(code, TestOutcome.Succeed));

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Code : "");
        (await CountBanks(code)).Should().Be(1);
        var envelopeJson = await fixture.Data.QueryScalarAsync<string>(
            "SELECT event_data::text FROM chargeback_diagram.domain_events WHERE event_type = 'test.bank.created' AND event_data->'data'->>'bankCode' = @code",
            new { code });
        using var envelope = JsonDocument.Parse(envelopeJson);
        envelope.RootElement.GetProperty("eventType").GetString().Should().Be("test.bank.created");
        envelope.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        envelope.RootElement.GetProperty("bankId").GetGuid().Should().Be(result.Value);
        envelope.RootElement.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(TestOutcome.ReturnFailure)]
    [InlineData(TestOutcome.Throw)]
    public async Task Failed_command_rolls_back_state_and_outbox(TestOutcome outcome)
    {
        var code = "RB" + Guid.NewGuid().ToString("N")[..10];

        try
        {
            var result = await SendAsync(new CreateTestBankCommand(code, outcome));
            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("TEST_FAILURE");
        }
        catch (InvalidOperationException) when (outcome == TestOutcome.Throw)
        {
        }

        (await CountBanks(code)).Should().Be(0);
        (await fixture.Data.QueryScalarAsync<long>(
            "SELECT count(*) FROM chargeback_diagram.domain_events WHERE event_data->'data'->>'bankCode' = @code", new { code }))
            .Should().Be(0);
    }

    [Fact]
    public async Task Outbox_dispatcher_publishes_each_event_once()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        foreach (var id in new[] { first, second })
        {
            await fixture.Data.ExecuteAsync(
                "INSERT INTO chargeback_diagram.domain_events(id, event_type, event_data) VALUES (@id, 'test.dispatch', CAST(@data AS jsonb))",
                new { id, data = $$"""{"eventId":"{{id}}"}""" });
        }

        var dispatcher = fixture.Factory.Services.GetRequiredService<OutboxDispatcher>();
        while (await dispatcher.DispatchBatchAsync(CancellationToken.None) > 0)
        {
        }

        var published = fixture.Publisher.Published.Where(p => p.DedupId == first.ToString() || p.DedupId == second.ToString()).ToList();
        published.Should().HaveCount(2);
        published.Should().OnlyContain(p => p.EventType == "test.dispatch");
        (await fixture.Data.QueryScalarAsync<long>(
            "SELECT count(*) FROM chargeback_diagram.domain_events WHERE id = ANY(@ids) AND published_at IS NOT NULL", new { ids = new[] { first, second } }))
            .Should().Be(2);

        await dispatcher.DispatchBatchAsync(CancellationToken.None);
        fixture.Publisher.Published.Count(p => p.DedupId == first.ToString()).Should().Be(1);
    }

    [Fact]
    public async Task Ai_decision_log_writer_persists_masked_audit_row()
    {
        var writer = fixture.Factory.Services.GetRequiredService<IAiDecisionLogWriter>();
        var id = Guid.NewGuid();

        await writer.WriteAsync(
            new AiDecisionLogEntry(id, null, AiAgents.Classification, AiCapabilities.DocumentVerification, "model-x", "doc-verify@1",
                new string('a', 64), """{"content":"x"}""", """{"suggestedType":"Invoice"}""", 12, 100, 20, DateTimeOffset.UtcNow),
            CancellationToken.None);

        (await fixture.Data.QueryScalarAsync<string>(
            "SELECT parsed_output->>'suggestedType' FROM chargeback_diagram.ai_decision_logs WHERE id = @id", new { id }))
            .Should().Be("Invoice");
    }

    private async Task<Chargeback.SharedKernel.Results.Result<Guid>> SendAsync(CreateTestBankCommand command)
    {
        var user = await fixture.Data.CreateUserWithPermissionsAsync("ADMIN", null);
        using var scope = fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.Sub)], "Test")),
            RequestServices = scope.ServiceProvider,
        };
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command);
    }

    private Task<long> CountBanks(string code) =>
        fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.banks WHERE bank_code = @code", new { code });
}
