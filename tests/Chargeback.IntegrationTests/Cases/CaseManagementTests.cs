using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargeback.Api.Common.Http;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Cases.Contracts;
using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Api.Features.Triage.Contracts;
using Chargeback.Api.Features.Triage.IssuerTriage;
using Chargeback.IntegrationTests.Infrastructure;
using Chargeback.IntegrationTests.Security;
using Chargeback.SharedKernel.Paging;
using Chargeback.SharedKernel.ValueObjects;
using Chargeback.TestSupport;

namespace Chargeback.IntegrationTests.Cases;

/// <summary>
/// Phase 7 end to end: intake → outbox → case creation worker → automatic triage worker → case APIs.
/// Gate evaluators, scheme rules and bank configuration used by the <c>Synthetic</c> host are SYNTHETIC fixtures;
/// they demonstrate mechanics only.
/// </summary>
[Collection(CaseCollection.Name)]
public sealed partial class CaseManagementTests(CaseFixture fixture)
{
    // ---- Case creation workflow ---------------------------------------------------------------------------

    [Fact]
    public async Task FLAGGED_dispute_gets_a_FLAGGED_case_in_the_analyst_queue_and_is_not_triaged()
    {
        var world = await World();
        var disputeId = await Submit(fixture.Default, world); // production gates: all PENDING_DEFINITION → FLAGGED

        await CaseFixture.DrainOutboxAsync(fixture.Default);

        var caseRow = await CaseFor(disputeId);
        caseRow.Status.Should().Be(CaseStatuses.Flagged);
        CaseReferencePattern().IsMatch(caseRow.Reference).Should().BeTrue(caseRow.Reference);
        (await Count("SELECT count(*) FROM chargeback_diagram.triage_results WHERE case_id = @id", caseRow.Id)).Should().Be(0);

        var queue = await Get<PagedResult<CaseSummaryDto>>(world.AnalystSub, $"/api/v1/cases?status=FLAGGED&bankId={world.BankId}");
        queue.Items.Should().ContainSingle(c => c.Id == caseRow.Id);
    }

    [Fact]
    public async Task NEW_dispute_gets_a_NEW_case_and_is_triaged_automatically()
    {
        var world = await World();
        await fixture.CreateSyntheticRuleAsync("SYN-C-" + world.Token[..6], MerchantIs(world.Merchant), "[]", 30);
        fixture.BankConfigurations.Set(world.BankId, Config(TriageOutcome.ProceedToFiling));
        var disputeId = await Submit(fixture.Synthetic, world); // SYNTHETIC passing gates → NEW

        await CaseFixture.DrainOutboxAsync(fixture.Synthetic);

        var caseRow = await CaseFor(disputeId);
        caseRow.Status.Should().Be(CaseStatuses.New, "triage records a recommendation; it never changes the status");
        var triage = await Get<List<TriageResultDto>>(world.AnalystSub, $"/api/v1/cases/{caseRow.Id}/triage");
        triage.Should().ContainSingle().Which.Outcome.Should().Be(TriageOutcome.ProceedToFiling);

        var timeline = await Get<List<CaseTimelineEntryDto>>(world.AnalystSub, $"/api/v1/cases/{caseRow.Id}/timeline");
        timeline.Select(e => e.EventType).Should().Equal("case.created", "triage.completed");
        timeline[1].Data!.Value.GetProperty("trigger").GetProperty("type").GetString().Should().Be("Automatic");
        (await Count("SELECT count(*) FROM chargeback_diagram.mastercom_filings WHERE case_id = @id", caseRow.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Redelivered_events_are_skipped_and_recorded_once()
    {
        var world = await World();
        fixture.BankConfigurations.Set(world.BankId, Config(TriageOutcome.Defer));
        await fixture.CreateSyntheticRuleAsync("SYN-R-" + world.Token[..6], MerchantIs(world.Merchant), "[]", 30);
        var disputeId = await Submit(fixture.Synthetic, world);
        await CaseFixture.DrainOutboxAsync(fixture.Synthetic);
        var caseRow = await CaseFor(disputeId);

        // Simulate at-least-once redelivery of both workflow events.
        await fixture.Data.ExecuteAsync(
            """
            UPDATE chargeback_diagram.domain_events SET published_at = NULL
            WHERE (event_type = 'dispute.gates.evaluated' AND event_data->'data'->>'disputeId' = @d)
               OR (event_type = 'case.created' AND case_id = @c)
            """,
            new { d = disputeId.ToString(), c = caseRow.Id });
        await CaseFixture.DrainOutboxAsync(fixture.Synthetic);

        (await Count("SELECT count(*) FROM chargeback_diagram.cases WHERE dispute_id = @id", disputeId)).Should().Be(1);
        (await Count("SELECT count(*) FROM chargeback_diagram.triage_results WHERE case_id = @id", caseRow.Id)).Should().Be(1);
        (await fixture.Data.QueryScalarAsync<long>(
            """
            SELECT count(*) FROM chargeback_diagram.processed_domain_events p
            JOIN chargeback_diagram.domain_events e ON e.id = p.event_id
            WHERE (e.event_type = 'dispute.gates.evaluated' AND e.event_data->'data'->>'disputeId' = @d) OR (e.event_type = 'case.created' AND e.case_id = @c)
            """,
            new { d = disputeId.ToString(), c = caseRow.Id })).Should().Be(2, "one processed record per consumed event");
    }

    [Fact]
    public async Task Case_references_follow_the_approved_format_and_come_from_the_database_sequence()
    {
        var world = await World();
        var first = await Submit(fixture.Default, world);
        var second = await Submit(fixture.Default, world);
        await CaseFixture.DrainOutboxAsync(fixture.Default);

        var a = (await CaseFor(first)).Reference;
        var b = (await CaseFor(second)).Reference;

        a.Should().MatchRegex($"^CB-{DateTimeOffset.UtcNow.Year}-\\d{{6}}$");
        Number(b).Should().BeGreaterThan(Number(a), "the number comes from a database sequence");
        (await fixture.Data.QueryScalarAsync<long>("SELECT last_value FROM chargeback_diagram.case_reference_seq")).Should().BeGreaterThanOrEqualTo(Number(b));
    }

    // ---- Queries ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Case_detail_has_derivation_etag_and_server_computed_days_remaining()
    {
        var world = await World();
        var rule = await fixture.CreateSyntheticRuleAsync("SYN-D-" + world.Token[..6], MerchantIs(world.Merchant), "[]", 30);
        fixture.BankConfigurations.Set(world.BankId, Config(TriageOutcome.ProceedToFiling));
        var disputeId = await Submit(fixture.Synthetic, world);
        await CaseFixture.DrainOutboxAsync(fixture.Synthetic);
        var caseRow = await CaseFor(disputeId);

        using var client = fixture.Synthetic.CreateClientFor(world.AnalystSub);
        var response = await client.GetAsync($"/api/v1/cases/{caseRow.Id}");
        var detail = (await response.Content.ReadFromJsonAsync<CaseDetailDto>(CurrentUserTests.Json))!;

        response.Headers.ETag!.Tag.Should().Be($"\"{detail.Version}\"");
        detail.DerivedReasonCode!.Id.Should().Be(rule.ReasonCodeId);
        detail.FilingDeadlineDate.Should().Be(new DateOnly(2026, 10, 1)); // synthetic: 2026-09-01 + 30 calendar days
        detail.DaysRemaining.Should().Be(new DateOnly(2026, 10, 1).DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber);
        detail.ValidActions.Should().Equal(CaseActions.Flag, CaseActions.StartReview);

        // Production host: calendar not approved → days remaining is not computed.
        using var prod = fixture.Default.CreateClientFor(world.AnalystSub);
        (await prod.GetFromJsonAsync<CaseDetailDto>($"/api/v1/cases/{caseRow.Id}", CurrentUserTests.Json))!.DaysRemaining.Should().BeNull();
    }

    [Fact]
    public async Task Case_list_is_scope_filtered_and_validates_status()
    {
        var world = await World();
        var other = await World();
        await Submit(fixture.Default, world);
        await Submit(fixture.Default, other);
        await CaseFixture.DrainOutboxAsync(fixture.Default);

        var mine = await Get<PagedResult<CaseSummaryDto>>(world.AnalystSub, "/api/v1/cases?pageSize=100");
        mine.Items.Should().NotBeEmpty().And.OnlyContain(c => c.BankId == world.BankId);
        (await Get<PagedResult<CaseSummaryDto>>(world.AnalystSub, $"/api/v1/cases?bankId={other.BankId}")).Items.Should().BeEmpty();

        using var client = fixture.Default.CreateClientFor(world.AnalystSub);
        (await client.GetAsync("/api/v1/cases?status=OPEN")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync($"/api/v1/cases/{(await CaseFor(await Submit(fixture.Default, other, drain: true))).Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Case_list_pages_20_by_default_rejects_out_of_range_paging_and_sorts_by_supported_fields_only()
    {
        var world = await World();
        for (var i = 0; i < 3; i++)
        {
            await Submit(fixture.Default, world);
        }

        await CaseFixture.DrainOutboxAsync(fixture.Default);
        var url = $"/api/v1/cases?bankId={world.BankId}";

        var byDefault = await Get<PagedResult<CaseSummaryDto>>(world.AnalystSub, url);
        byDefault.PageSize.Should().Be(20);
        byDefault.Items.Should().HaveCount(3).And.BeInDescendingOrder(c => c.CreatedAt);

        var ascending = await Get<PagedResult<CaseSummaryDto>>(world.AnalystSub, url + "&sortBy=caseReference&sortDirection=asc");
        ascending.Items.Select(c => c.CaseReference).Should().BeInAscendingOrder(StringComparer.Ordinal);
        var descending = await Get<PagedResult<CaseSummaryDto>>(world.AnalystSub, url + "&sortBy=caseReference&sortDirection=DESC");
        descending.Items.Select(c => c.CaseReference).Should().Equal(ascending.Items.Select(c => c.CaseReference).Reverse());

        using var client = fixture.Default.CreateClientFor(world.AnalystSub);
        var unsupported = await client.GetAsync(url + "&sortBy=case_reference");
        unsupported.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await unsupported.Content.ReadAsStringAsync()).Should().Contain("INVALID_SORT_FIELD");
        var badDirection = await client.GetAsync(url + "&sortBy=createdAt&sortDirection=up");
        badDirection.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await badDirection.Content.ReadAsStringAsync()).Should().Contain("sortDirection");

        foreach (var outOfRange in new[] { "&pageSize=0", "&pageSize=101", "&page=0" })
        {
            var refused = await client.GetAsync(url + outOfRange);
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, outOfRange);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("VALIDATION_FAILED");
        }

        // Stub list endpoints validate the sort contract before returning 501.
        var stub = await client.GetAsync("/api/v1/reviews/queue?sortBy=bogus");
        stub.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await stub.Content.ReadAsStringAsync()).Should().Contain("INVALID_SORT_FIELD");
    }

    // ---- Status transitions (POST /transitions, common guide v1.4 §3.2) ----------------------------------

    [Fact]
    public async Task Analyst_starts_review_on_a_flagged_case_with_rationale_and_expected_version()
    {
        var (world, caseId) = await FlaggedCase();
        var version = await VersionOf(world.AnalystSub, caseId);

        var response = await Transition(world.AnalystSub, caseId, TransitionBody("START_REVIEW", version, "picked up from queue"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var detail = (await response.Content.ReadFromJsonAsync<CaseDetailDto>(CurrentUserTests.Json))!;
        detail.Status.Should().Be(CaseStatuses.UnderReview);
        detail.Version.Should().NotBe(version);
        detail.ValidActions.Should().BeEmpty("APPROVE/REJECT need REVIEW_CASE, which this analyst does not hold");

        var changed = (await Get<List<CaseTimelineEntryDto>>(world.AnalystSub, $"/api/v1/cases/{caseId}/timeline")).Last();
        changed.EventType.Should().Be("case.status.changed");
        changed.Data!.Value.GetProperty("action").GetString().Should().Be("START_REVIEW");
        changed.Data!.Value.GetProperty("fromStatus").GetString().Should().Be("FLAGGED");
        changed.Data!.Value.GetProperty("toStatus").GetString().Should().Be("UNDER_REVIEW");
        changed.Data!.Value.GetProperty("reason").GetString().Should().Be("picked up from queue");
        changed.Data!.Value.GetProperty("changedBy").GetGuid().Should().Be(world.AnalystId);
    }

    [Theory]
    [InlineData("APPROVE")]   // only via the Human Review decision (Phase 9)
    [InlineData("REJECT")]
    [InlineData("FILE")]      // only via human filing confirmation (Phase 10)
    [InlineData("CLOSE")]     // FLAGGED cannot be closed
    [InlineData("FLAG")]      // FLAG applies to NEW cases only
    public async Task Actions_not_available_here_are_refused_with_422_and_nothing_is_recorded(string action)
    {
        var (world, caseId) = await FlaggedCase();
        var version = await VersionOf(world.AnalystSub, caseId);

        var response = await Transition(world.AnalystSub, caseId, TransitionBody(action, version, "try"));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("INVALID_TRANSITION");
        (await Count("SELECT count(*) FROM chargeback_diagram.domain_events WHERE case_id = @id AND event_type = 'case.status.changed'", caseId)).Should().Be(0);
        (await CaseFor(caseId, byCaseId: true)).Status.Should().Be(CaseStatuses.Flagged);
    }

    [Fact]
    public async Task Analyst_unflags_and_flags_a_case_without_touching_the_dispute()
    {
        var (world, caseId) = await FlaggedCase();

        var unflagged = await Transition(world.AnalystSub, caseId, TransitionBody("UNFLAG", await VersionOf(world.AnalystSub, caseId), "facts checked"));
        unflagged.StatusCode.Should().Be(HttpStatusCode.OK, await unflagged.Content.ReadAsStringAsync());
        var asNew = (await unflagged.Content.ReadFromJsonAsync<CaseDetailDto>(CurrentUserTests.Json))!;
        asNew.Status.Should().Be(CaseStatuses.New);
        asNew.ValidActions.Should().Equal(CaseActions.Flag, CaseActions.StartReview);

        var flagged = await Transition(world.AnalystSub, caseId, TransitionBody("FLAG", asNew.Version));
        flagged.StatusCode.Should().Be(HttpStatusCode.OK, await flagged.Content.ReadAsStringAsync());
        (await flagged.Content.ReadFromJsonAsync<CaseDetailDto>(CurrentUserTests.Json))!.ValidActions.Should().Equal(CaseActions.Unflag, CaseActions.StartReview);

        var changes = (await Get<List<CaseTimelineEntryDto>>(world.AnalystSub, $"/api/v1/cases/{caseId}/timeline"))
            .Where(e => e.EventType == "case.status.changed").Select(e => e.Data!.Value.GetProperty("action").GetString()).ToList();
        changes.Should().Equal("UNFLAG", "FLAG");
        (await fixture.Data.QueryScalarAsync<string>(
            "SELECT d.status FROM chargeback_diagram.disputes d JOIN chargeback_diagram.cases c ON c.dispute_id = d.id WHERE c.id = @caseId", new { caseId }))
            .Should().Be("FLAGGED", "FLAG/UNFLAG change only the case status");
        (await Count("SELECT count(*) FROM chargeback_diagram.triage_results WHERE case_id = @id", caseId)).Should().Be(0, "UNFLAG does not trigger triage");
    }

    [Fact]
    public async Task Transition_requires_expected_version_known_action_rationale_and_idempotency_key()
    {
        var (world, caseId) = await FlaggedCase();
        var version = await VersionOf(world.AnalystSub, caseId);

        (await Transition(world.AnalystSub, caseId, """{"action":"START_REVIEW"}""")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Transition(world.AnalystSub, caseId, TransitionBody("OPEN", version))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Transition(world.AnalystSub, caseId, TransitionBody("CLOSE", version))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Transition(world.AnalystSub, caseId, TransitionBody("START_REVIEW", version, "card 4111111111111111"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Transition(world.AnalystSub, caseId, TransitionBody("START_REVIEW", version), key: null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var stale = await Transition(world.AnalystSub, caseId, TransitionBody("START_REVIEW", version + 1));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.Content.ReadAsStringAsync()).Should().Contain("CASE_VERSION_MISMATCH");

        (await Count("SELECT count(*) FROM chargeback_diagram.domain_events WHERE case_id = @id AND event_type = 'case.status.changed'", caseId)).Should().Be(0);
    }

    [Fact]
    public async Task Transition_is_permission_and_scope_protected()
    {
        var (world, caseId) = await FlaggedCase();
        var version = await VersionOf(world.AnalystSub, caseId);
        var readOnly = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases);
        await fixture.Data.GrantScopeAsync(readOnly.Id, world.BankId);
        var otherBank = await World();
        var body = TransitionBody("START_REVIEW", version);

        (await Transition(readOnly.Sub, caseId, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Transition(otherBank.AnalystSub, caseId, body)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Transition(world.BankUserSub, caseId, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Transition_is_idempotent_per_key()
    {
        var (world, caseId) = await FlaggedCase();
        var body = TransitionBody("START_REVIEW", await VersionOf(world.AnalystSub, caseId), "picked up");
        var key = "idem-" + Guid.NewGuid().ToString("N");

        var first = await Transition(world.AnalystSub, caseId, body, key);
        var replay = await Transition(world.AnalystSub, caseId, body, key);

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        replay.StatusCode.Should().Be(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());
        replay.Headers.GetValues(IdempotencyKey.ReplayedHeaderName).Should().Equal("true");
        (await replay.Content.ReadFromJsonAsync<CaseDetailDto>(CurrentUserTests.Json))!.Status.Should().Be(CaseStatuses.UnderReview);
        (await Count("SELECT count(*) FROM chargeback_diagram.domain_events WHERE case_id = @id AND event_type = 'case.status.changed'", caseId)).Should().Be(1);

        var reused = await Transition(world.AnalystSub, caseId, TransitionBody("START_REVIEW", 1, "different"), key);
        reused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await reused.Content.ReadAsStringAsync()).Should().Contain("IDEMPOTENCY_KEY_REUSED");
    }

    [Fact]
    public async Task Close_needs_a_rationale_and_filed_cases_are_admin_only()
    {
        var (world, caseId) = await FlaggedCase();
        var admin = await fixture.Data.CreateUserWithPermissionsAsync("ADMIN", null, Permissions.ViewCases, Permissions.UpdateCaseStatus);
        await fixture.Data.GrantScopeAsync(admin.Id, world.BankId);

        // REJECTED and FILED are reached through Phase 9/10 workflows; set them directly for this test.
        await fixture.Data.ExecuteAsync("UPDATE chargeback_diagram.cases SET status = 'REJECTED' WHERE id = @caseId", new { caseId });
        var rejected = await Get<CaseDetailDto>(world.AnalystSub, $"/api/v1/cases/{caseId}");
        rejected.ValidActions.Should().Equal(CaseActions.Close);
        var closed = await Transition(world.AnalystSub, caseId, TransitionBody("CLOSE", rejected.Version, "client notified"));
        closed.StatusCode.Should().Be(HttpStatusCode.OK, await closed.Content.ReadAsStringAsync());
        (await CaseFor(caseId, byCaseId: true)).Status.Should().Be(CaseStatuses.Closed);

        await fixture.Data.ExecuteAsync("UPDATE chargeback_diagram.cases SET status = 'FILED' WHERE id = @caseId", new { caseId });
        var filed = await Get<CaseDetailDto>(world.AnalystSub, $"/api/v1/cases/{caseId}");
        filed.ValidActions.Should().BeEmpty("FILED → CLOSED is admin only");
        (await Transition(world.AnalystSub, caseId, TransitionBody("CLOSE", filed.Version, "done"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await Get<CaseDetailDto>(admin.Sub, $"/api/v1/cases/{caseId}")).ValidActions.Should().Equal(CaseActions.Close);
        (await Transition(admin.Sub, caseId, TransitionBody("CLOSE", filed.Version, "scheme outcome recorded"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CaseFor(caseId, byCaseId: true)).Status.Should().Be(CaseStatuses.Closed);
    }

    [Fact]
    public async Task Patch_status_endpoint_no_longer_exists()
    {
        var (world, caseId) = await FlaggedCase();

        (await Patch(world.AnalystSub, $"/api/v1/cases/{caseId}/status", """{"status":"UNDER_REVIEW","reason":"x"}""", await VersionOf(world.AnalystSub, caseId)))
            .StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Database_rejects_statuses_outside_the_approved_set()
    {
        var (_, caseId) = await FlaggedCase();

        var act = () => fixture.Data.ExecuteAsync("UPDATE chargeback_diagram.cases SET status = 'OPEN' WHERE id = @caseId", new { caseId });

        await act.Should().ThrowAsync<Npgsql.PostgresException>().Where(e => e.ConstraintName == "cases_status_check");
    }

    // ---- Assignment ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Case_can_be_assigned_to_an_eligible_analyst_only()
    {
        var (world, caseId) = await FlaggedCase();
        var eligible = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases);
        await fixture.Data.GrantScopeAsync(eligible.Id, world.BankId);
        var noScope = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases);
        var noPermission = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewBanks);
        await fixture.Data.GrantScopeAsync(noPermission.Id, world.BankId);

        foreach (var ineligible in new[] { noScope.Id, noPermission.Id, world.BankUserId, Guid.NewGuid() })
        {
            var refused = await Patch(world.AnalystSub, $"/api/v1/cases/{caseId}/assignment", $$"""{"assignedTo":"{{ineligible}}"}""", await VersionOf(world.AnalystSub, caseId));
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        var assigned = await Patch(world.AnalystSub, $"/api/v1/cases/{caseId}/assignment", $$"""{"assignedTo":"{{eligible.Id}}"}""", await VersionOf(world.AnalystSub, caseId));
        assigned.StatusCode.Should().Be(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync());
        (await assigned.Content.ReadFromJsonAsync<CaseDetailDto>(CurrentUserTests.Json))!.AssignedTo.Should().Be(eligible.Id);

        var unassigned = await Patch(world.AnalystSub, $"/api/v1/cases/{caseId}/assignment", """{"assignedTo":null}""", await VersionOf(world.AnalystSub, caseId));
        (await unassigned.Content.ReadFromJsonAsync<CaseDetailDto>(CurrentUserTests.Json))!.AssignedTo.Should().BeNull();

        (await Count("SELECT count(*) FROM chargeback_diagram.domain_events WHERE case_id = @id AND event_type = 'case.assigned'", caseId)).Should().Be(2);
    }

    [Fact]
    public async Task Assignment_requires_assign_case_permission()
    {
        var (world, caseId) = await FlaggedCase();
        var statusOnly = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases, Permissions.UpdateCaseStatus);
        await fixture.Data.GrantScopeAsync(statusOnly.Id, world.BankId);

        (await Patch(statusOnly.Sub, $"/api/v1/cases/{caseId}/assignment", """{"assignedTo":null}""", await VersionOf(world.AnalystSub, caseId)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Manual re-triage (ADR-0124) ----------------------------------------------------------------------

    [Fact]
    public async Task Analyst_can_retriage_a_flagged_case_with_a_recorded_reason()
    {
        var (world, caseId) = await FlaggedCase();

        var response = await Post(world.AnalystSub, $"/api/v1/cases/{caseId}/retriage", """{"reason":"facts corrected by bank"}""");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var timeline = await Get<List<CaseTimelineEntryDto>>(world.AnalystSub, $"/api/v1/cases/{caseId}/timeline");
        timeline.Select(e => e.EventType).Should().Equal("case.created", "case.retriage.requested", "triage.completed");
        timeline[1].Data!.Value.GetProperty("reason").GetString().Should().Be("facts corrected by bank");
        var trigger = timeline[2].Data!.Value.GetProperty("trigger");
        trigger.GetProperty("type").GetString().Should().Be("Manual");
        trigger.GetProperty("requestedBy").GetGuid().Should().Be(world.AnalystId);
        (await CaseFor(caseId, byCaseId: true)).Status.Should().Be(CaseStatuses.Flagged, "re-triage never changes the status");
    }

    [Fact]
    public async Task Retriage_is_refused_outside_flagged_and_under_review_and_needs_a_reason()
    {
        var world = await World();
        fixture.BankConfigurations.Set(world.BankId, Config(TriageOutcome.Defer));
        var disputeId = await Submit(fixture.Synthetic, world); // NEW case
        await CaseFixture.DrainOutboxAsync(fixture.Synthetic);
        var newCase = await CaseFor(disputeId);

        var refused = await Post(world.AnalystSub, $"/api/v1/cases/{newCase.Id}/retriage", """{"reason":"why not"}""");
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("RETRIAGE_NOT_ALLOWED");

        var (flaggedWorld, flaggedCase) = await FlaggedCase();
        (await Post(flaggedWorld.AnalystSub, $"/api/v1/cases/{flaggedCase}/retriage", """{"reason":""}""")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Post(flaggedWorld.AnalystSub, $"/api/v1/cases/{flaggedCase}/retriage", "{}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var viewer = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases);
        await fixture.Data.GrantScopeAsync(viewer.Id, flaggedWorld.BankId);
        (await Post(viewer.Sub, $"/api/v1/cases/{flaggedCase}/retriage", """{"reason":"x"}""")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var statusOnly = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases, Permissions.UpdateCaseStatus);
        await fixture.Data.GrantScopeAsync(statusOnly.Id, flaggedWorld.BankId);
        (await Post(statusOnly.Sub, $"/api/v1/cases/{flaggedCase}/retriage", """{"reason":"x"}""")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "re-triage requires RETRIAGE_CASE, not UPDATE_CASE_STATUS");
        (await Post(world.AnalystSub, $"/api/v1/cases/{flaggedCase}/retriage", """{"reason":"x"}""")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retriage_is_allowed_under_review()
    {
        var (world, caseId) = await FlaggedCase();
        (await Transition(world.AnalystSub, caseId, TransitionBody("START_REVIEW", await VersionOf(world.AnalystSub, caseId), "review")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await Post(world.AnalystSub, $"/api/v1/cases/{caseId}/retriage", """{"reason":"second look"}""")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Timeline_is_for_analysts_only()
    {
        var (world, caseId) = await FlaggedCase();
        using var bank = fixture.Default.CreateClientFor(world.BankUserSub);

        (await bank.GetAsync($"/api/v1/cases/{caseId}/timeline")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Migration 0003 -----------------------------------------------------------------------------------

    [Fact]
    public async Task Migration_0003_is_idempotent_and_adds_only_the_approved_objects()
    {
        var database = "mig3_" + Guid.NewGuid().ToString("N")[..10];
        await fixture.Data.ExecuteAsync($"CREATE DATABASE {database}");
        var target = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        await BaselineDatabase.ApplyAsync(target);
        var migration = BaselineDatabase.MigrationPath("0003_case_management.sql");

        await BaselineDatabase.ExecuteScriptAsync(target, migration);
        await BaselineDatabase.ExecuteScriptAsync(target, migration);

        var data = new TestData(target);
        (await data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.permissions WHERE name = 'ASSIGN_CASE'")).Should().Be(1);
        (await data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.role_permissions")).Should().Be(0, "no role is assigned any permission");
        (await data.QueryScalarAsync<long>("SELECT count(*) FROM pg_constraint WHERE conname = 'cases_status_check'")).Should().Be(1);
        (await data.QueryScalarAsync<long>("SELECT max_value FROM pg_sequences WHERE sequencename = 'case_reference_seq'")).Should().Be(999_999);
        (await data.QueryScalarAsync<long>(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'processed_domain_events_event_id_key' AND contype = 'u'")).Should().Be(1);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private sealed record TestWorld(Guid BankId, string BankUserSub, Guid BankUserId, string AnalystSub, Guid AnalystId, string Merchant, string Token);

    private sealed record CaseRow(Guid Id, string Reference, string Status);

    private async Task<TestWorld> World()
    {
        var bankId = await fixture.Data.CreateBankAsync();
        var bankUser = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId, Permissions.CreateDispute, Permissions.ViewCases);
        var analyst = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null,
            Permissions.ViewCases, Permissions.UpdateCaseStatus, Permissions.AssignCase, Permissions.ViewTriage, Permissions.RetriageCase);
        await fixture.Data.GrantScopeAsync(analyst.Id, bankId);
        var token = new string(Guid.NewGuid().ToString("N").Select(ch => char.IsAsciiDigit(ch) ? (char)('g' + (ch - '0')) : ch).ToArray());
        return new TestWorld(bankId, bankUser.Sub, bankUser.Id, analyst.Sub, analyst.Id, "SYN merchant " + token, token);
    }

    private async Task<(TestWorld World, Guid CaseId)> FlaggedCase()
    {
        var world = await World();
        var disputeId = await Submit(fixture.Default, world, drain: true);
        return (world, (await CaseFor(disputeId)).Id);
    }

    private static async Task<Guid> Submit(ChargebackApiFactory host, TestWorld world, bool drain = false)
    {
        using var client = host.CreateClientFor(world.BankUserSub);
        client.DefaultRequestHeaders.Add(IdempotencyKey.HeaderName, "idem-" + Guid.NewGuid().ToString("N"));
        var body = $$"""
            {"bankId":"{{world.BankId}}","transactionDate":"2026-09-01T10:00:00Z","transactionAmount":80.00,
             "currencyCode":"GBP","merchantName":"{{world.Merchant}}"}
            """;
        var response = await client.PostAsync("/api/v1/intake/disputes", new StringContent(body, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var disputeId = (await response.Content.ReadFromJsonAsync<DisputeAcceptedResponse>(CurrentUserTests.Json))!.DisputeId;
        if (drain)
        {
            await CaseFixture.DrainOutboxAsync(host);
        }

        return disputeId;
    }

    private async Task<CaseRow> CaseFor(Guid id, bool byCaseId = false)
    {
        var json = await fixture.Data.QueryScalarAsync<string>(
            byCaseId
                ? "SELECT json_build_object('id', id, 'reference', case_reference, 'status', status)::text FROM chargeback_diagram.cases WHERE id = @id"
                : "SELECT json_build_object('id', id, 'reference', case_reference, 'status', status)::text FROM chargeback_diagram.cases WHERE dispute_id = @id",
            new { id });
        return JsonSerializer.Deserialize<CaseRow>(json, CurrentUserTests.Json)!;
    }

    private Task<long> Count(string sql, Guid id) => fixture.Data.QueryScalarAsync<long>(sql, new { id });

    private async Task<T> Get<T>(string sub, string url)
    {
        using var client = fixture.Synthetic.CreateClientFor(sub);
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(CurrentUserTests.Json))!;
    }

    private async Task<uint> VersionOf(string sub, Guid caseId) => (await Get<CaseDetailDto>(sub, $"/api/v1/cases/{caseId}")).Version;

    private async Task<HttpResponseMessage> Patch(string sub, string url, string body, uint? version)
    {
        using var client = fixture.Default.CreateClientFor(sub);
        using var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (version is { } v)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{v}\"");
        }

        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> Transition(string sub, Guid caseId, string body, string? key = "")
    {
        using var client = fixture.Default.CreateClientFor(sub);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/cases/{caseId}/transitions") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (key is not null)
        {
            request.Headers.Add(IdempotencyKey.HeaderName, key.Length == 0 ? "idem-" + Guid.NewGuid().ToString("N") : key);
        }

        return await client.SendAsync(request);
    }

    private static string TransitionBody(string action, uint? expectedVersion, string? rationale = null) =>
        JsonSerializer.Serialize(new { action, rationale, expectedVersion });

    private async Task<HttpResponseMessage> Post(string sub, string url, string body)
    {
        using var client = fixture.Default.CreateClientFor(sub);
        return await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
    }

    private static long Number(string reference) => long.Parse(reference[^6..], System.Globalization.CultureInfo.InvariantCulture);

    private static string MerchantIs(string merchant) => $$"""{"fact":"dispute.merchantName","op":"eq","value":"{{merchant}}"}""";

    /// <summary>SYNTHETIC bank configuration routing every eligible case to <paramref name="outcome"/>.</summary>
    private static BankTriageConfiguration Config(TriageOutcome outcome) => new(
        "SYN-CONFIG-CASES",
        [new("SYN-ELIG", """{"fact":"dispute.currencyCode","op":"exists"}""", TriageOutcome.Invalid)],
        new RiskScoringModel([], 100m),
        [],
        [new("SYN-ROUTE", """{"fact":"dispute.intakeChannel","op":"eq","value":"PORTAL"}""", outcome)]);

    [GeneratedRegex(@"^CB-\d{4}-\d{6}$")]
    private static partial Regex CaseReferencePattern();
}
