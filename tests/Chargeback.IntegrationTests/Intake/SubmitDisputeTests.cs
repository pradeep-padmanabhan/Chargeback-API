using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chargeback.Api.Common.Http;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Api.Features.Intake.Gates;
using Chargeback.IntegrationTests.Infrastructure;
using Chargeback.IntegrationTests.Security;

namespace Chargeback.IntegrationTests.Intake;

/// <summary>Phase 5 intake skeleton end to end: endpoint → pipeline → gate engine → PostgreSQL + outbox.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SubmitDisputeTests(PostgresFixture fixture)
{
    private const string ValidBody = """
        {
          "bankId": "{BANK}",
          "cardholderReference": "CH-001",
          "cardNumberMasked": "XXXX XXXX XXXX 4242",
          "transactionDate": "2026-09-01T12:30:00+01:00",
          "transactionAmount": 125.50,
          "currencyCode": "gbp",
          "acquirerReferenceNumber": "74537604221431003881552",
          "merchantName": "Example Store"
        }
        """;

    [Fact]
    public async Task Dispute_is_persisted_with_ten_gate_results_and_flagged_pending_sign_off()
    {
        var bankId = await fixture.Data.CreateBankAsync();
        var bankUser = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId, Permissions.CreateDispute, Permissions.ViewCases);

        var response = await Submit(bankUser.Sub, ValidBody.Replace("{BANK}", bankId.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var accepted = (await response.Content.ReadFromJsonAsync<DisputeAcceptedResponse>(CurrentUserTests.Json))!;
        accepted.Status.Should().Be(DisputeStatuses.Flagged);
        response.Headers.Location!.ToString().Should().Be($"/api/v1/disputes/{accepted.DisputeId}");

        // Stored masked, normalised, channel inferred server-side.
        using var client = fixture.Factory.CreateClientFor(bankUser.Sub);
        var dispute = (await client.GetFromJsonAsync<DisputeDto>($"/api/v1/disputes/{accepted.DisputeId}", CurrentUserTests.Json))!;
        dispute.BankId.Should().Be(bankId);
        dispute.CardNumberMasked.Should().Be("************4242");
        dispute.CurrencyCode.Should().Be("GBP");
        dispute.TransactionAmount.Should().Be(125.50m);
        dispute.TransactionDate.Should().Be(new DateTimeOffset(2026, 9, 1, 11, 30, 0, TimeSpan.Zero));
        dispute.IntakeChannel.Should().Be(IntakeChannel.Portal);
        dispute.Status.Should().Be("FLAGGED");

        (await fixture.Data.QueryScalarAsync<long>(
            "SELECT count(*) FROM chargeback_diagram.gate_results WHERE dispute_id = @Id AND passed IS NULL AND flag_reason LIKE 'PENDING_DEFINITION%'",
            new { Id = accepted.DisputeId })).Should().Be(10);
    }

    [Fact]
    public async Task Outbox_events_are_written_with_the_dispute()
    {
        var bankId = await fixture.Data.CreateBankAsync();
        var bankUser = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId, Permissions.CreateDispute);

        var response = await Submit(bankUser.Sub, ValidBody.Replace("{BANK}", bankId.ToString()));
        var disputeId = (await response.Content.ReadFromJsonAsync<DisputeAcceptedResponse>(CurrentUserTests.Json))!.DisputeId;

        var types = await fixture.Data.QueryScalarAsync<string>(
            """
            SELECT string_agg(event_type, ',' ORDER BY event_type) FROM chargeback_diagram.domain_events
            WHERE (event_data->>'bankId')::uuid = @bankId
            """,
            new { bankId });
        types.Should().Be("dispute.flagged,dispute.gates.evaluated,dispute.received");

        var evaluated = await fixture.Data.QueryScalarAsync<string>(
            "SELECT event_data::text FROM chargeback_diagram.domain_events WHERE event_type = 'dispute.gates.evaluated' AND event_data->'data'->>'disputeId' = @id",
            new { id = disputeId.ToString() });
        using var doc = JsonDocument.Parse(evaluated);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("registryVersion").GetString().Should().Be(ApprovedGateRegistry.RegistryVersion);
        data.GetProperty("undecidedGates").GetArrayLength().Should().Be(10);
        evaluated.Should().NotContain("************4242", "event payloads carry no card data at all")
            .And.NotContainAny(["cardNumber", "cardholderReference"]);
    }

    [Fact]
    public async Task Analyst_sees_the_complete_gate_trail_in_order_bank_user_does_not()
    {
        var bankId = await fixture.Data.CreateBankAsync();
        var bankUser = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId, Permissions.CreateDispute, Permissions.ViewCases);
        var analyst = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases);
        await fixture.Data.GrantScopeAsync(analyst.Id, bankId);
        var response = await Submit(bankUser.Sub, ValidBody.Replace("{BANK}", bankId.ToString()));
        var disputeId = (await response.Content.ReadFromJsonAsync<DisputeAcceptedResponse>(CurrentUserTests.Json))!.DisputeId;

        using var analystClient = fixture.Factory.CreateClientFor(analyst.Sub);
        var gates = (await analystClient.GetFromJsonAsync<List<GateResultDto>>($"/api/v1/disputes/{disputeId}/gate-results", CurrentUserTests.Json))!;

        gates.Select(g => g.GateNumber).Should().Equal(Enumerable.Range(1, 10));
        gates.Select(g => g.GateName).Should().Equal(new ApprovedGateRegistry().Gates.Select(g => g.Name));
        gates.Should().OnlyContain(g => g.Passed == null && g.CheckedAt != null);

        using var bankClient = fixture.Factory.CreateClientFor(bankUser.Sub);
        (await bankClient.GetAsync($"/api/v1/disputes/{disputeId}/gate-results")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("cardNumberMasked", "4111111111111111")]
    [InlineData("merchantName", "paid with 4111 1111 1111 1111")]
    public async Task Card_numbers_are_rejected_and_never_stored(string field, string value)
    {
        var bankId = await fixture.Data.CreateBankAsync();
        var bankUser = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId, Permissions.CreateDispute);
        var body = $$"""{"bankId":"{{bankId}}","{{field}}":"{{value}}"}""";

        var response = await Submit(bankUser.Sub, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("VALIDATION_FAILED");
        problem.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
        (await response.Content.ReadAsStringAsync()).Should().NotContain("4111", "error bodies must not echo card data");
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.disputes WHERE bank_id = @bankId", new { bankId }))
            .Should().Be(0);
    }

    [Fact]
    public async Task Submitting_for_another_bank_is_not_found_and_creates_nothing()
    {
        var own = await fixture.Data.CreateBankAsync();
        var foreign = await fixture.Data.CreateBankAsync();
        var bankUser = await fixture.Data.CreateUserWithPermissionsAsync("BANK", own, Permissions.CreateDispute);

        var response = await Submit(bankUser.Sub, ValidBody.Replace("{BANK}", foreign.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.disputes WHERE bank_id = @foreign", new { foreign }))
            .Should().Be(0);
    }

    [Fact]
    public async Task Processor_can_submit_only_for_scoped_banks()
    {
        var scoped = await fixture.Data.CreateBankAsync();
        var other = await fixture.Data.CreateBankAsync();
        var processor = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.CreateDispute);
        await fixture.Data.GrantScopeAsync(processor.Id, scoped);

        (await Submit(processor.Sub, ValidBody.Replace("{BANK}", scoped.ToString()))).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Submit(processor.Sub, ValidBody.Replace("{BANK}", other.ToString()))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_create_dispute_permission_submission_is_forbidden()
    {
        var bankId = await fixture.Data.CreateBankAsync();
        var bankUser = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId, Permissions.ViewCases);

        (await Submit(bankUser.Sub, ValidBody.Replace("{BANK}", bankId.ToString()))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<HttpResponseMessage> Submit(string sub, string body)
    {
        using var client = fixture.Factory.CreateClientFor(sub);
        client.DefaultRequestHeaders.Add(IdempotencyKey.HeaderName, "idem-" + Guid.NewGuid().ToString("N"));
        return await client.PostAsync("/api/v1/intake/disputes", new StringContent(body, Encoding.UTF8, "application/json"));
    }
}
