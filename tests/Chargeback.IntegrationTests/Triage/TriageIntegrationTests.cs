using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chargeback.Api.Common.Http;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Api.Features.Triage.Contracts;
using Chargeback.Api.Features.Triage.EvaluateCaseTriage;
using Chargeback.Api.Features.Triage.IssuerTriage;
using Chargeback.Api.Features.Triage.SchemeRules;
using Chargeback.IntegrationTests.Security;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.ValueObjects;
using Chargeback.TestSupport;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Chargeback.IntegrationTests.Triage;

/// <summary>
/// Phase 5 → Phase 6 integration: intake → ten gates → scheme rules → issuer triage → persistence/audit.
/// ALL RULE, CONFIGURATION AND GATE DATA HERE IS SYNTHETIC; results demonstrate the mechanics only and do not
/// validate any real scheme or bank business rule. Each test uses a unique merchant name so the synthetic
/// rules of other tests evaluate false (scheme rules are global data).
/// </summary>
[Collection(TriageCollection.Name)]
public sealed class TriageIntegrationTests(TriageFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task NEW_dispute_is_triaged_end_to_end_with_synthetic_rules_and_configuration()
    {
        var world = await World();
        var rule = await fixture.CreateSyntheticRuleAsync(
            "SYN-E2E-" + world.Token[..6],
            MerchantIs(world.Merchant),
            """[{"slotName":"SYN cardholder letter","required":true,"expectedType":"SYN-LETTER"}]""",
            timeLimitDays: 30);
        fixture.BankConfigurations.Set(world.BankId, Config(TriageOutcome.ProceedToFiling));

        // Phase 5: intake with SYNTHETIC passing gates → NEW.
        var (disputeId, status) = await Submit(fixture.Synthetic, world);
        status.Should().Be(DisputeStatuses.New);
        var caseId = await fixture.CreateSyntheticCaseAsync(disputeId);

        // Phase 6: workflow step.
        var result = await RunTriage(fixture.Synthetic, caseId);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Code : "");
        result.Value.EvaluationStatus.Should().Be(TriageEvaluationStatus.Complete);
        result.Value.Outcome.Should().Be(TriageOutcome.ProceedToFiling);
        result.Value.TriageLayer.Should().Be(IssuerTriageEngine.IssuerLayer);
        result.Value.RoutingPolicyOutcome.Should().Be("SYN-ROUTE");
        result.Value.HardEligibilityPass.Should().BeTrue();

        // Case carries the deterministic derivation; transaction date 2026-09-01 (UTC) + 30 calendar days.
        (await fixture.Data.QueryScalarAsync<Guid>("SELECT derived_reason_code FROM chargeback_diagram.cases WHERE id = @caseId", new { caseId }))
            .Should().Be(rule.ReasonCodeId);
        (await fixture.Data.QueryScalarAsync<DateTime>("SELECT filing_deadline_date FROM chargeback_diagram.cases WHERE id = @caseId", new { caseId }))
            .Should().Be(new DateTime(2026, 10, 1));

        // The outcome is a recommendation only: no filing, no status change.
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.mastercom_filings WHERE case_id = @caseId", new { caseId })).Should().Be(0);
        (await fixture.Data.QueryScalarAsync<string>("SELECT status FROM chargeback_diagram.cases WHERE id = @caseId", new { caseId })).Should().Be("NEW");

        // Analysts can read it.
        var analyst = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewTriage);
        await fixture.Data.GrantScopeAsync(analyst.Id, world.BankId);
        using var client = fixture.Synthetic.CreateClientFor(analyst.Sub);
        var listed = (await client.GetFromJsonAsync<List<TriageResultDto>>($"/api/v1/cases/{caseId}/triage", CurrentUserTests.Json))!;
        listed.Should().ContainSingle().Which.Id.Should().Be(result.Value.Id);

        // Demonstration capture (SYNTHETIC data) for the phase report.
        output.WriteLine("DEMO dispute.status=" + status);
        output.WriteLine("DEMO gates=" + await fixture.Data.QueryScalarAsync<string>("SELECT string_agg(gate_number || ':' || coalesce(passed::text,'null'), ',' ORDER BY gate_number) FROM chargeback_diagram.gate_results WHERE dispute_id = @disputeId", new { disputeId }));
        output.WriteLine("DEMO triage_result=" + JsonSerializer.Serialize(listed[0], CurrentUserTests.Json));
        output.WriteLine("DEMO triage.completed=" + await fixture.Data.QueryScalarAsync<string>("SELECT (event_data->'data')::text FROM chargeback_diagram.domain_events WHERE event_type = 'triage.completed' AND case_id = @caseId", new { caseId }));
    }

    [Fact]
    public async Task Triage_audit_event_records_rule_version_configuration_and_documents_without_card_data()
    {
        var world = await World();
        var rule = await fixture.CreateSyntheticRuleAsync("SYN-AUD-" + world.Token[..6], MerchantIs(world.Merchant),
            """[{"slotName":"SYN proof","required":false}]""", timeLimitDays: 10);
        fixture.BankConfigurations.Set(world.BankId, Config(TriageOutcome.AutoRefund));
        var (disputeId, _) = await Submit(fixture.Synthetic, world);
        var caseId = await fixture.CreateSyntheticCaseAsync(disputeId);

        var result = await RunTriage(fixture.Synthetic, caseId);
        result.Value.Outcome.Should().Be(TriageOutcome.AutoRefund);

        var json = await fixture.Data.QueryScalarAsync<string>(
            "SELECT event_data::text FROM chargeback_diagram.domain_events WHERE event_type = 'triage.completed' AND case_id = @caseId", new { caseId });
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("status").GetString().Should().Be("Complete");
        data.GetProperty("outcome").GetString().Should().Be("AutoRefund");
        var scheme = data.GetProperty("scheme");
        scheme.GetProperty("status").GetString().Should().Be("Determined");
        scheme.GetProperty("ruleSpecId").GetGuid().Should().Be(rule.RuleSpecId);
        scheme.GetProperty("deadlineStatus").GetString().Should().Be("Calculated");
        scheme.GetProperty("requiredDocuments")[0].GetProperty("slotName").GetString().Should().Be("SYN proof");
        data.GetProperty("issuer").GetProperty("configurationVersion").GetString().Should().Be("SYN-CONFIG-IT");
        json.Should().NotContain("************").And.NotContain("cardNumber");

        // AutoRefund is recorded only; no financial action exists in the platform.
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.mastercom_filings WHERE case_id = @caseId", new { caseId })).Should().Be(0);
    }

    [Fact]
    public async Task FLAGGED_dispute_never_progresses_to_triage()
    {
        var world = await World();
        await fixture.CreateSyntheticRuleAsync("SYN-FLG-" + world.Token[..6], MerchantIs(world.Merchant), "[]", 30);
        fixture.BankConfigurations.Set(world.BankId, Config(TriageOutcome.ProceedToFiling));

        // Production gate configuration: every gate PENDING_DEFINITION → FLAGGED.
        var (disputeId, status) = await Submit(fixture.Default, world);
        status.Should().Be(DisputeStatuses.Flagged);
        var caseId = await fixture.CreateSyntheticCaseAsync(disputeId);

        var result = await RunTriage(fixture.Synthetic, caseId);

        result.Error.Should().Be(TriageErrors.DisputeNotEligible);
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.triage_results WHERE case_id = @caseId", new { caseId })).Should().Be(0);
    }

    [Fact]
    public async Task Missing_bank_configuration_is_recorded_incomplete_and_routed_for_manual_attention()
    {
        var world = await World();
        var rule = await fixture.CreateSyntheticRuleAsync("SYN-NOCFG-" + world.Token[..6], MerchantIs(world.Merchant), "[]", 30);
        var (disputeId, _) = await Submit(fixture.Synthetic, world); // no bank configuration registered
        var caseId = await fixture.CreateSyntheticCaseAsync(disputeId);

        var result = await RunTriage(fixture.Synthetic, caseId);

        result.Value.EvaluationStatus.Should().Be(TriageEvaluationStatus.Incomplete);
        result.Value.Outcome.Should().BeNull();
        result.Value.HumanReviewTriggered.Should().BeTrue();
        result.Value.HumanReviewReason.Should().StartWith(IssuerTriageEngine.IncompletePrefix);
        (await fixture.Data.QueryScalarAsync<string>("SELECT outcome IS NULL AND human_review_triggered FROM chargeback_diagram.triage_results WHERE case_id = @caseId", new { caseId })
            ).Should().Be("True");
        (await fixture.Data.QueryScalarAsync<Guid>("SELECT derived_reason_code FROM chargeback_diagram.cases WHERE id = @caseId", new { caseId }))
            .Should().Be(rule.ReasonCodeId, "the scheme layer was determined even though issuer configuration is missing");
    }

    [Fact]
    public async Task Production_configuration_never_produces_a_definitive_outcome()
    {
        var world = await World();
        await fixture.CreateSyntheticRuleAsync("SYN-PROD-" + world.Token[..6], MerchantIs(world.Merchant), "[]", 30);
        var (disputeId, _) = await Submit(fixture.Synthetic, world);
        var caseId = await fixture.CreateSyntheticCaseAsync(disputeId);

        // Default host: no scheme settings approved → ConfigurationPending → incomplete, nothing derived.
        var result = await RunTriage(fixture.Default, caseId);

        result.Value.EvaluationStatus.Should().Be(TriageEvaluationStatus.Incomplete);
        result.Value.TriageLayer.Should().Be(IssuerTriageEngine.SchemeLayer);
        result.Value.HumanReviewReason.Should().Contain("ConfigurationPending");
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.cases WHERE id = @caseId AND derived_reason_code IS NULL", new { caseId }))
            .Should().Be(1);
    }

    [Fact]
    public async Task No_matching_approved_rule_is_incomplete_and_draft_rules_are_ignored()
    {
        var world = await World();
        await fixture.CreateSyntheticRuleAsync("SYN-DRAFT-" + world.Token[..6], MerchantIs(world.Merchant), "[]", 30, approvalStatus: "DRAFT");
        fixture.BankConfigurations.Set(world.BankId, Config(TriageOutcome.ProceedToFiling));
        var (disputeId, _) = await Submit(fixture.Synthetic, world);
        var caseId = await fixture.CreateSyntheticCaseAsync(disputeId);

        var result = await RunTriage(fixture.Synthetic, caseId);

        result.Value.EvaluationStatus.Should().Be(TriageEvaluationStatus.Incomplete);
        result.Value.Outcome.Should().BeNull();
        result.Value.HumanReviewReason.Should().Contain("NoMatch");
    }

    [Fact]
    public async Task Expired_rule_version_is_not_used()
    {
        var world = await World();
        await fixture.CreateSyntheticRuleAsync("SYN-EXP-" + world.Token[..6], MerchantIs(world.Merchant), "[]", 30,
            effectiveFrom: new DateOnly(2020, 1, 1), effectiveTo: new DateOnly(2026, 8, 31)); // transaction date is 2026-09-01
        fixture.BankConfigurations.Set(world.BankId, Config(TriageOutcome.ProceedToFiling));
        var (disputeId, _) = await Submit(fixture.Synthetic, world);
        var caseId = await fixture.CreateSyntheticCaseAsync(disputeId);

        (await RunTriage(fixture.Synthetic, caseId)).Value.Outcome.Should().BeNull();
    }

    [Fact]
    public async Task Database_failure_while_reading_rules_records_nothing()
    {
        var world = await World();
        fixture.BankConfigurations.Set(world.BankId, Config(TriageOutcome.ProceedToFiling));
        var (disputeId, _) = await Submit(fixture.Synthetic, world);
        var caseId = await fixture.CreateSyntheticCaseAsync(disputeId);
        await using var failing = new ChargebackApiFactory(fixture.ConnectionString)
        {
            Settings = TriageFixture.SyntheticSchemeSettings,
            ConfigureServices = s => s.AddScoped<ISchemeRuleRepository, FailingRuleRepository>(),
        };

        var result = await RunTriage(failing, caseId);

        result.Error.Should().Be(TriageErrors.Unavailable);
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.triage_results WHERE case_id = @caseId", new { caseId })).Should().Be(0);
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.domain_events WHERE case_id = @caseId", new { caseId })).Should().Be(0);
    }

    [Fact]
    public async Task Unavailable_bank_configuration_records_nothing()
    {
        var world = await World();
        await fixture.CreateSyntheticRuleAsync("SYN-UNAV-" + world.Token[..6], MerchantIs(world.Merchant), "[]", 30);
        fixture.BankConfigurations.SetUnavailable(world.BankId);
        var (disputeId, _) = await Submit(fixture.Synthetic, world);
        var caseId = await fixture.CreateSyntheticCaseAsync(disputeId);

        (await RunTriage(fixture.Synthetic, caseId)).Error.Should().Be(TriageErrors.Unavailable);
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.triage_results WHERE case_id = @caseId", new { caseId })).Should().Be(0);
    }

    [Fact]
    public async Task Triage_cannot_be_run_by_users_and_results_are_permission_and_scope_protected()
    {
        var world = await World();
        var (disputeId, _) = await Submit(fixture.Synthetic, world);
        var caseId = await fixture.CreateSyntheticCaseAsync(disputeId);

        // Outside SystemExecution (as any user request would be) the workflow step is refused.
        using (var scope = fixture.Synthetic.Services.CreateScope())
        {
            var refused = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new EvaluateCaseTriageCommand(caseId));
            refused.Error.Code.Should().Be("SYSTEM_OPERATION_ONLY");
        }

        var scoped = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewTriage);
        await fixture.Data.GrantScopeAsync(scoped.Id, world.BankId);
        var otherBank = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewTriage);
        await fixture.Data.GrantScopeAsync(otherBank.Id, await fixture.Data.CreateBankAsync());
        var noPermission = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases);
        await fixture.Data.GrantScopeAsync(noPermission.Id, world.BankId);
        var bankUser = await fixture.Data.CreateUserWithPermissionsAsync("BANK", world.BankId, Permissions.ViewTriage);

        (await Get(scoped.Sub, caseId)).Should().Be(HttpStatusCode.OK);
        (await Get(otherBank.Sub, caseId)).Should().Be(HttpStatusCode.NotFound);
        (await Get(noPermission.Sub, caseId)).Should().Be(HttpStatusCode.Forbidden);
        (await Get(bankUser.Sub, caseId)).Should().Be(HttpStatusCode.Forbidden, "triage internals are for analysts only");
    }

    private async Task<HttpStatusCode> Get(string sub, Guid caseId)
    {
        using var client = fixture.Synthetic.CreateClientFor(sub);
        return (await client.GetAsync($"/api/v1/cases/{caseId}/triage")).StatusCode;
    }

    private sealed record TestWorld(Guid BankId, string Sub, string Merchant, string Token);

    private async Task<TestWorld> World()
    {
        var bankId = await fixture.Data.CreateBankAsync();
        var user = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId, Permissions.CreateDispute);
        // Letters only: a random hex token can contain 13+ consecutive digits, which intake rightly rejects as a possible card number.
        var token = new string(Guid.NewGuid().ToString("N").Select(ch => char.IsAsciiDigit(ch) ? (char)('g' + (ch - '0')) : ch).ToArray());
        return new TestWorld(bankId, user.Sub, "SYN merchant " + token, token);
    }

    private static async Task<(Guid DisputeId, string Status)> Submit(ChargebackApiFactory factory, TestWorld world)
    {
        using var client = factory.CreateClientFor(world.Sub);
        client.DefaultRequestHeaders.Add(IdempotencyKey.HeaderName, "idem-" + Guid.NewGuid().ToString("N"));
        var body = $$"""
            {"bankId":"{{world.BankId}}","cardNumberMasked":"************4242","transactionDate":"2026-09-01T10:00:00Z",
             "transactionAmount":120.00,"currencyCode":"GBP","merchantName":"{{world.Merchant}}"}
            """;
        var response = await client.PostAsync("/api/v1/intake/disputes", new StringContent(body, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var accepted = (await response.Content.ReadFromJsonAsync<DisputeAcceptedResponse>(CurrentUserTests.Json))!;
        return (accepted.DisputeId, accepted.Status);
    }

    private static async Task<Result<TriageResultDto>> RunTriage(ChargebackApiFactory factory, Guid caseId)
    {
        using var scope = factory.Services.CreateScope();
        using (SystemExecution.Begin("integration-test: triage after intake"))
        {
            return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new EvaluateCaseTriageCommand(caseId));
        }
    }

    private static string MerchantIs(string merchant) => $$"""{"fact":"dispute.merchantName","op":"eq","value":"{{merchant}}"}""";

    /// <summary>SYNTHETIC bank configuration routing every eligible case to <paramref name="outcome"/>.</summary>
    private static BankTriageConfiguration Config(TriageOutcome outcome) => new(
        "SYN-CONFIG-IT",
        [new("SYN-ELIG", """{"fact":"dispute.currencyCode","op":"exists"}""", TriageOutcome.Invalid)],
        new RiskScoringModel([new("SYN-RISK", """{"fact":"dispute.transactionAmount","op":"gte","value":100000}""", 100m)], 100m),
        [],
        [new("SYN-ROUTE", """{"fact":"dispute.intakeChannel","op":"eq","value":"PORTAL"}""", outcome)]);

    private sealed class FailingRuleRepository : ISchemeRuleRepository
    {
        public Task<IReadOnlyList<SchemeRuleSpecRecord>> GetAllAsync(CancellationToken cancellationToken) =>
            throw new Npgsql.NpgsqlException("SYNTHETIC database failure");
    }
}
