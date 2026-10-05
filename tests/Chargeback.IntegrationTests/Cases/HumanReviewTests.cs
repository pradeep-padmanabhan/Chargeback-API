using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chargeback.Api.Common.Http;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Cases.Contracts;
using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Api.Features.Review.Contracts;
using Chargeback.Api.Features.Triage.IssuerTriage;
using Chargeback.Api.Workers;
using Chargeback.Infrastructure.Outbox;
using Chargeback.IntegrationTests.Security;
using Chargeback.SharedKernel.Paging;
using Chargeback.SharedKernel.ValueObjects;
using Chargeback.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Chargeback.IntegrationTests.Cases;

/// <summary>
/// Human Review end to end: intake → case → automatic triage → START_REVIEW → one-time AI summary → queue →
/// workspace → decision. Rules, bank configuration and the summarizer on the <c>Synthetic</c> host are SYNTHETIC.
/// </summary>
[Collection(CaseCollection.Name)]
public sealed class HumanReviewTests(CaseFixture fixture)
{
    [Fact]
    public async Task Analyst_reviews_and_approves_a_triaged_case_with_its_derived_reason_code()
    {
        var (world, caseId, reasonCodeId) = await DerivedCaseUnderReview();

        // One-time summary, generated from the deterministic facts only.
        var call = fixture.Summarizer.CallsFor(caseId).Should().ContainSingle().Subject;
        call.Outcome.Should().Be(TriageOutcome.ProceedToFiling);
        call.RequiredDocuments.Should().Equal("SYN-RECEIPT");
        call.Gates.Should().HaveCount(10);

        var queue = await Get<PagedResult<ReviewQueueItemDto>>(world.ReviewerSub, "/api/v1/reviews/queue?pageSize=100");
        queue.Items.Should().ContainSingle(i => i.CaseId == caseId).Which.Status.Should().Be(CaseStatuses.UnderReview);

        var workspace = await Get<ReviewWorkspaceDto>(world.ReviewerSub, $"/api/v1/cases/{caseId}/review");
        workspace.Case.Status.Should().Be(CaseStatuses.UnderReview);
        workspace.Case.DerivedReasonCode!.Id.Should().Be(reasonCodeId);
        workspace.Case.ValidActions.Should().Equal(CaseActions.Approve, CaseActions.Reject);
        workspace.Dispute.BankId.Should().Be(world.BankId);
        workspace.Gates.Should().HaveCount(10);
        workspace.Triage.Should().ContainSingle().Which.Outcome.Should().Be(TriageOutcome.ProceedToFiling);
        workspace.DocumentChecklist.Should().BeEmpty("checklist snapshots arrive with Phase 8");
        workspace.AiSummary!.Text.Should().Be(FakeTriageSummarizer.TextFor(caseId, 1));
        workspace.AiSummary.Advisory.Should().BeTrue();
        workspace.AiSummary.ModelName.Should().Be(FakeTriageSummarizer.ModelName);
        workspace.AiSummary.PromptTemplateId.Should().Be(FakeTriageSummarizer.PromptTemplateId);
        workspace.Activity.Should().Contain(e => e.EventType == "case.status.changed");

        var response = await Decide(world.ReviewerSub, caseId, Body("Approve", "evidence supports the claim", reasonCodeId, workspace.Case.Version));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var decision = (await response.Content.ReadFromJsonAsync<ReviewDecisionResponse>(CurrentUserTests.Json))!;
        decision.Decision.Should().Be(ReviewDecision.Approve);
        decision.ReasonCodeId.Should().Be(reasonCodeId);
        decision.ReviewedBy.Should().Be(world.ReviewerId);
        decision.Case.Status.Should().Be(CaseStatuses.Approved);
        decision.Case.HumanReview!.Verdict.Should().Be(CaseStatuses.Approved);
        decision.Case.ValidActions.Should().BeEmpty("FILE needs SUBMIT_MASTERCOM");

        var row = await Row(
            "SELECT json_build_object('decision', decision, 'reasonCodeId', reason_code_id, 'rationale', rationale, 'reviewedBy', reviewed_by)::text FROM chargeback_diagram.case_review_decisions WHERE case_id = @id",
            caseId);
        row.GetProperty("decision").GetString().Should().Be("APPROVED");
        row.GetProperty("reasonCodeId").GetGuid().Should().Be(reasonCodeId);
        row.GetProperty("rationale").GetString().Should().Be("evidence supports the claim");

        var timeline = await Get<List<CaseTimelineEntryDto>>(world.ReviewerSub, $"/api/v1/cases/{caseId}/timeline");
        timeline.Select(e => e.EventType).TakeLast(2).Should().Equal("case.review.decided", "case.status.changed");
        timeline.Last().Data!.Value.GetProperty("action").GetString().Should().Be("APPROVE");

        (await Get<PagedResult<ReviewQueueItemDto>>(world.ReviewerSub, "/api/v1/reviews/queue?pageSize=100")).Items.Should().NotContain(i => i.CaseId == caseId);
    }

    [Fact]
    public async Task Summary_is_written_once_and_never_regenerated()
    {
        var (_, caseId, _) = await DerivedCaseUnderReview();

        // Redelivery of a status change to UNDER_REVIEW (new event id) must not call the model again.
        await DeliverStatusChange(caseId, CaseStatuses.UnderReview);
        await DeliverStatusChange(caseId, CaseStatuses.UnderReview);

        fixture.Summarizer.CallsFor(caseId).Should().ContainSingle();
        (await fixture.Data.QueryScalarAsync<string>("SELECT ai_summary FROM chargeback_diagram.cases WHERE id = @caseId", new { caseId }))
            .Should().Be(FakeTriageSummarizer.TextFor(caseId, 1));
    }

    [Fact]
    public async Task Unavailable_ai_leaves_no_summary_and_is_not_retried()
    {
        var (world, caseId, _) = await DerivedCaseUnderReview(beforeReview: id => fixture.Summarizer.MakeUnavailableFor(id));

        fixture.Summarizer.CallsFor(caseId).Should().ContainSingle();
        (await Get<ReviewWorkspaceDto>(world.ReviewerSub, $"/api/v1/cases/{caseId}/review")).AiSummary.Should().BeNull();
        (await fixture.Data.QueryScalarAsync<long>(
            "SELECT count(*) FROM chargeback_diagram.processed_domain_events WHERE consumer = @consumer AND event_id IN (SELECT id FROM chargeback_diagram.domain_events WHERE case_id = @caseId)",
            new { consumer = ReviewSummaryConsumer.ConsumerName, caseId })).Should().Be(1, "the event is recorded as processed, so it is not redelivered");
    }

    [Fact]
    public async Task Flagged_case_without_a_derived_code_can_be_rejected_but_not_approved()
    {
        var (world, caseId) = await FlaggedCaseUnderReview();
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.cases WHERE id = @caseId AND ai_summary IS NULL", new { caseId }))
            .Should().Be(1, "nothing deterministic to explain: FLAGGED cases are never triaged automatically");
        var version = await VersionOf(world.ReviewerSub, caseId);

        var approve = await Decide(world.ReviewerSub, caseId, Body("Approve", "looks fine", Guid.NewGuid(), version));
        approve.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await approve.Content.ReadAsStringAsync()).Should().Contain("REASON_CODE_NOT_DERIVED");

        (await Decide(world.ReviewerSub, caseId, Body("Reject", "no basis", Guid.NewGuid(), version))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "reasonCodeId applies only to Approve");

        var reject = await Decide(world.ReviewerSub, caseId, Body("Reject", "no basis for a chargeback", null, version));
        reject.StatusCode.Should().Be(HttpStatusCode.OK, await reject.Content.ReadAsStringAsync());
        var decision = (await reject.Content.ReadFromJsonAsync<ReviewDecisionResponse>(CurrentUserTests.Json))!;
        decision.Case.Status.Should().Be(CaseStatuses.Rejected);
        decision.ReasonCodeId.Should().BeNull();
        decision.Case.ValidActions.Should().Equal(CaseActions.Close);
    }

    [Fact]
    public async Task Decision_requires_under_review_matching_code_current_version_and_well_formed_body()
    {
        var (world, caseId, reasonCodeId) = await DerivedCaseUnderReview();
        var version = await VersionOf(world.ReviewerSub, caseId);

        var mismatch = await Decide(world.ReviewerSub, caseId, Body("Approve", "x", Guid.NewGuid(), version));
        mismatch.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await mismatch.Content.ReadAsStringAsync()).Should().Contain("REASON_CODE_MISMATCH");

        var stale = await Decide(world.ReviewerSub, caseId, Body("Approve", "x", reasonCodeId, version + 1));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.Content.ReadAsStringAsync()).Should().Contain("CASE_VERSION_MISMATCH");

        (await Decide(world.ReviewerSub, caseId, """{"rationale":"x","expectedVersion":1}""")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Decide(world.ReviewerSub, caseId, Body("Approve", "", reasonCodeId, version))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Decide(world.ReviewerSub, caseId, Body("Approve", null, reasonCodeId, null))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Decide(world.ReviewerSub, caseId, Body("Approve", "card 4111111111111111", reasonCodeId, version))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Decide(world.ReviewerSub, caseId, Body("Approve", "x", reasonCodeId, version), key: null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Count("SELECT count(*) FROM chargeback_diagram.case_review_decisions WHERE case_id = @id", caseId)).Should().Be(0);

        var (flaggedWorld, flagged) = await FlaggedCase();
        var notUnderReview = await Decide(flaggedWorld.ReviewerSub, flagged, Body("Reject", "x", null, await VersionOf(flaggedWorld.ReviewerSub, flagged)));
        notUnderReview.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await notUnderReview.Content.ReadAsStringAsync()).Should().Contain("INVALID_TRANSITION");
    }

    [Fact]
    public async Task Review_is_permission_and_scope_protected()
    {
        var (world, caseId) = await FlaggedCaseUnderReview();
        var version = await VersionOf(world.ReviewerSub, caseId);
        var noReview = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases, Permissions.UpdateCaseStatus);
        await fixture.Data.GrantScopeAsync(noReview.Id, world.BankId);
        var otherBank = await World();
        var body = Body("Reject", "x", null, version);

        using (var client = fixture.Default.CreateClientFor(noReview.Sub))
        {
            (await client.GetAsync("/api/v1/reviews/queue")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await client.GetAsync($"/api/v1/cases/{caseId}/review")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await Decide(noReview.Sub, caseId, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Decide(world.BankUserSub, caseId, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Decide(otherBank.ReviewerSub, caseId, body)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var client = fixture.Default.CreateClientFor(otherBank.ReviewerSub))
        {
            (await client.GetAsync($"/api/v1/cases/{caseId}/review")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        (await Get<PagedResult<ReviewQueueItemDto>>(otherBank.ReviewerSub, "/api/v1/reviews/queue?pageSize=100")).Items.Should().NotContain(i => i.CaseId == caseId);
    }

    [Fact]
    public async Task Decision_is_idempotent_per_key()
    {
        var (world, caseId) = await FlaggedCaseUnderReview();
        var body = Body("Reject", "no basis", null, await VersionOf(world.ReviewerSub, caseId));
        var key = "idem-" + Guid.NewGuid().ToString("N");

        var first = await Decide(world.ReviewerSub, caseId, body, key);
        var replay = await Decide(world.ReviewerSub, caseId, body, key);

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.Headers.GetValues(IdempotencyKey.ReplayedHeaderName).Should().Equal("true");
        (await Count("SELECT count(*) FROM chargeback_diagram.case_review_decisions WHERE case_id = @id", caseId)).Should().Be(1);
    }

    [Fact]
    public async Task Decisions_are_append_only()
    {
        var (world, caseId) = await FlaggedCaseUnderReview();
        (await Decide(world.ReviewerSub, caseId, Body("Reject", "no basis", null, await VersionOf(world.ReviewerSub, caseId)))).StatusCode.Should().Be(HttpStatusCode.OK);

        var update = () => fixture.Data.ExecuteAsync("UPDATE chargeback_diagram.case_review_decisions SET rationale = 'changed' WHERE case_id = @caseId", new { caseId });
        var delete = () => fixture.Data.ExecuteAsync("DELETE FROM chargeback_diagram.case_review_decisions WHERE case_id = @caseId", new { caseId });

        await update.Should().ThrowAsync<Npgsql.PostgresException>().WithMessage("*append-only*");
        await delete.Should().ThrowAsync<Npgsql.PostgresException>().WithMessage("*append-only*");
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private sealed record TestWorld(Guid BankId, string BankUserSub, string ReviewerSub, Guid ReviewerId, string Merchant, string Token);

    private async Task<TestWorld> World()
    {
        var bankId = await fixture.Data.CreateBankAsync();
        var bankUser = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId, Permissions.CreateDispute, Permissions.ViewCases);
        var reviewer = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null,
            Permissions.ViewCases, Permissions.UpdateCaseStatus, Permissions.ViewTriage, Permissions.ReviewCase);
        await fixture.Data.GrantScopeAsync(reviewer.Id, bankId);
        var token = new string(Guid.NewGuid().ToString("N").Select(ch => char.IsAsciiDigit(ch) ? (char)('g' + (ch - '0')) : ch).ToArray());
        return new TestWorld(bankId, bankUser.Sub, reviewer.Sub, reviewer.Id, "SYN merchant " + token, token);
    }

    /// <summary>NEW case, triaged with a SYNTHETIC rule (derived reason code), then taken into review.</summary>
    private async Task<(TestWorld World, Guid CaseId, Guid ReasonCodeId)> DerivedCaseUnderReview(Action<Guid>? beforeReview = null)
    {
        var world = await World();
        var rule = await fixture.CreateSyntheticRuleAsync(
            "SYN-R-" + world.Token[..6], MerchantIs(world.Merchant), """[{"slotName":"SYN-RECEIPT","required":true}]""", 30);
        fixture.BankConfigurations.Set(world.BankId, Config(TriageOutcome.ProceedToFiling));
        var disputeId = await Submit(fixture.Synthetic, world);
        await CaseFixture.DrainOutboxAsync(fixture.Synthetic);
        var caseId = await CaseIdFor(disputeId);
        beforeReview?.Invoke(caseId);

        await StartReview(fixture.Synthetic, world.ReviewerSub, caseId);
        await CaseFixture.DrainOutboxAsync(fixture.Synthetic);
        return (world, caseId, rule.ReasonCodeId);
    }

    private async Task<(TestWorld World, Guid CaseId)> FlaggedCase()
    {
        var world = await World();
        var disputeId = await Submit(fixture.Default, world);
        await CaseFixture.DrainOutboxAsync(fixture.Default);
        return (world, await CaseIdFor(disputeId));
    }

    private async Task<(TestWorld World, Guid CaseId)> FlaggedCaseUnderReview()
    {
        var (world, caseId) = await FlaggedCase();
        await StartReview(fixture.Default, world.ReviewerSub, caseId);
        await CaseFixture.DrainOutboxAsync(fixture.Default);
        return (world, caseId);
    }

    private async Task StartReview(ChargebackApiFactory host, string sub, Guid caseId)
    {
        using var client = host.CreateClientFor(sub);
        client.DefaultRequestHeaders.Add(IdempotencyKey.HeaderName, "idem-" + Guid.NewGuid().ToString("N"));
        var body = JsonSerializer.Serialize(new { action = CaseActions.StartReview, expectedVersion = await VersionOf(sub, caseId) });
        var response = await client.PostAsync($"/api/v1/cases/{caseId}/transitions", new StringContent(body, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Simulates (re)delivery of a <c>case.status.changed</c> event with a fresh event id.</summary>
    private async Task DeliverStatusChange(Guid caseId, string toStatus)
    {
        await using var scope = fixture.Synthetic.Services.CreateAsyncScope();
        var consumer = scope.ServiceProvider.GetServices<IIntegrationEventConsumer>().OfType<ReviewSummaryConsumer>().Single();
        var data = JsonSerializer.SerializeToElement(new { toStatus });
        await consumer.HandleAsync(
            new IntegrationEventEnvelope(Guid.NewGuid(), ReviewSummaryConsumer.SourceEventType, 1, DateTimeOffset.UtcNow, "test", null, caseId, data),
            CancellationToken.None);
    }

    private static async Task<Guid> Submit(ChargebackApiFactory host, TestWorld world)
    {
        using var client = host.CreateClientFor(world.BankUserSub);
        client.DefaultRequestHeaders.Add(IdempotencyKey.HeaderName, "idem-" + Guid.NewGuid().ToString("N"));
        var body = $$"""
            {"bankId":"{{world.BankId}}","transactionDate":"2026-09-01T10:00:00Z","transactionAmount":80.00,
             "currencyCode":"GBP","merchantName":"{{world.Merchant}}"}
            """;
        var response = await client.PostAsync("/api/v1/intake/disputes", new StringContent(body, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DisputeAcceptedResponse>(CurrentUserTests.Json))!.DisputeId;
    }

    private Task<Guid> CaseIdFor(Guid disputeId) =>
        fixture.Data.QueryScalarAsync<Guid>("SELECT id FROM chargeback_diagram.cases WHERE dispute_id = @disputeId", new { disputeId });

    private Task<long> Count(string sql, Guid id) => fixture.Data.QueryScalarAsync<long>(sql, new { id });

    private async Task<JsonElement> Row(string sql, Guid id) =>
        JsonDocument.Parse(await fixture.Data.QueryScalarAsync<string>(sql, new { id })).RootElement.Clone();

    private async Task<T> Get<T>(string sub, string url)
    {
        using var client = fixture.Default.CreateClientFor(sub);
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(CurrentUserTests.Json))!;
    }

    private async Task<uint> VersionOf(string sub, Guid caseId) => (await Get<CaseDetailDto>(sub, $"/api/v1/cases/{caseId}")).Version;

    private async Task<HttpResponseMessage> Decide(string sub, Guid caseId, string body, string? key = "")
    {
        using var client = fixture.Default.CreateClientFor(sub);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/cases/{caseId}/review/decision")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (key is not null)
        {
            request.Headers.Add(IdempotencyKey.HeaderName, key.Length == 0 ? "idem-" + Guid.NewGuid().ToString("N") : key);
        }

        return await client.SendAsync(request);
    }

    private static string Body(string decision, string? rationale, Guid? reasonCodeId, uint? expectedVersion) =>
        JsonSerializer.Serialize(new { decision, rationale, reasonCodeId, expectedVersion });

    private static string MerchantIs(string merchant) => $$"""{"fact":"dispute.merchantName","op":"eq","value":"{{merchant}}"}""";

    /// <summary>SYNTHETIC bank configuration routing every eligible case to <paramref name="outcome"/>.</summary>
    private static BankTriageConfiguration Config(TriageOutcome outcome) => new(
        "SYN-CONFIG-REVIEW",
        [new("SYN-ELIG", """{"fact":"dispute.currencyCode","op":"exists"}""", TriageOutcome.Invalid)],
        new RiskScoringModel([], 100m),
        [],
        [new("SYN-ROUTE", """{"fact":"dispute.intakeChannel","op":"eq","value":"PORTAL"}""", outcome)]);
}
