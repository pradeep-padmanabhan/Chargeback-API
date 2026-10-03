using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.ClientPortal.Contracts;
using Chargeback.IntegrationTests.Infrastructure;
using Chargeback.SharedKernel.Paging;
using Chargeback.SharedKernel.ValueObjects;
using Dapper;
using Npgsql;

namespace Chargeback.IntegrationTests.Security;

/// <summary>Client Portal &amp; Communications: bank-scoped curated views, the two-way thread, and the Zendesk stub.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ClientPortalTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Portal_user_sees_only_its_own_banks_cases_in_a_curated_projection()
    {
        var own = await PortalWorld();
        var other = await PortalWorld();

        var page = await GetOk<PagedResult<PortalCaseSummaryDto>>(own.BankUserSub, "/api/v1/portal/cases?pageSize=100");

        page.PageSize.Should().Be(100);
        page.Items.Select(c => c.CaseId).Should().BeEquivalentTo(own.CaseIds);
        page.Items.Should().BeInDescendingOrder(c => c.CreatedAt, "default sort is createdAt desc");
        var first = page.Items.Single(c => c.CaseId == own.CaseIds[0]);
        first.Amount.Should().Be(80.00m);
        first.Currency.Should().Be("GBP");
        first.MerchantName.Should().Be("SYN merchant");
        first.ReferenceNumber.Should().StartWith("CB-");

        // The projection carries only the approved fields.
        var raw = await Get(own.BankUserSub, "/api/v1/portal/cases");
        var item = JsonDocument.Parse(await raw.Content.ReadAsStringAsync()).RootElement.GetProperty("items")[0];
        item.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["caseId", "referenceNumber", "status", "amount", "currency", "merchantName", "createdAt", "updatedAt"]);
        JsonDocument.Parse(await raw.Content.ReadAsStringAsync()).RootElement.GetProperty("pageSize").GetInt32().Should().Be(20);

        (await GetOk<PagedResult<PortalCaseSummaryDto>>(own.BankUserSub, "/api/v1/portal/cases?status=CLOSED")).Items.Should().BeEmpty();
        (await GetOk<PagedResult<PortalCaseSummaryDto>>(own.BankUserSub, "/api/v1/portal/cases?status=FLAGGED")).Items.Should().HaveCount(2);
        (await Get(own.BankUserSub, "/api/v1/portal/cases?status=OPEN")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetOk<PagedResult<PortalCaseSummaryDto>>(other.BankUserSub, "/api/v1/portal/cases")).Items.Should().NotContain(c => own.CaseIds.Contains(c.CaseId));
    }

    [Fact]
    public async Task Case_detail_adds_reason_deadline_and_confirmed_documents_and_hides_other_banks()
    {
        var own = await PortalWorld();
        var other = await PortalWorld();
        var caseId = own.CaseIds[0];
        var reasonCodeId = await fixture.Data.QueryScalarAsync<Guid>(
            "INSERT INTO chargeback_diagram.scheme_reason_codes(code, description, effective_from) VALUES (@code, 'SYNTHETIC reason', DATE '2020-01-01') RETURNING id",
            new { code = "SYN-P-" + Guid.NewGuid().ToString("N")[..6] });
        await fixture.Data.ExecuteAsync(
            "UPDATE chargeback_diagram.cases SET derived_reason_code = @reasonCodeId, filing_deadline_date = DATE '2026-11-30' WHERE id = @caseId",
            new { reasonCodeId, caseId });
        await fixture.Data.ExecuteAsync(
            """
            INSERT INTO chargeback_diagram.documents(case_id, file_name, s3_key, scheme_stage, upload_status, document_status)
            VALUES (@caseId, 'receipt.pdf', 'k1', 'Initial', 'UPLOADED', 'Failed'),
                   (@caseId, 'declared-only.pdf', 'k2', 'Initial', 'PENDING_UPLOAD', 'Pending')
            """,
            new { caseId });

        var detail = await GetOk<PortalCaseDetailDto>(own.BankUserSub, $"/api/v1/portal/cases/{caseId}");

        detail.ReasonDescription.Should().Be("SYNTHETIC reason");
        detail.ReasonCode.Should().StartWith("SYN-P-");
        detail.NetworkDeadline.Should().Be(new DateOnly(2026, 11, 30));
        detail.Documents.Should().ContainSingle().Which.Should().Be(new PortalDocumentDto("receipt.pdf", DocumentStatus.Failed));
        var raw = JsonDocument.Parse(await (await Get(own.BankUserSub, $"/api/v1/portal/cases/{caseId}")).Content.ReadAsStringAsync()).RootElement;
        raw.TryGetProperty("aiSummary", out _).Should().BeFalse();
        raw.TryGetProperty("assignedTo", out _).Should().BeFalse();
        raw.GetProperty("documents")[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["name", "processingStatus"]);

        (await Get(other.BankUserSub, $"/api/v1/portal/cases/{caseId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Get(own.BankUserSub, $"/api/v1/portal/cases/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Get(own.AnalystSub, $"/api/v1/portal/cases/{caseId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "the portal is for bank users");
    }

    [Fact]
    public async Task Bank_user_and_analyst_exchange_messages_on_the_case_thread()
    {
        var own = await PortalWorld();
        var other = await PortalWorld();
        var caseId = own.CaseIds[0];

        var posted = await Post(own.BankUserSub, $"/api/v1/portal/cases/{caseId}/messages", """{"body":"  Where is my refund?  "}""");
        posted.StatusCode.Should().Be(HttpStatusCode.Created, await posted.Content.ReadAsStringAsync());

        var analystView = await GetOk<List<CaseMessageDto>>(own.AnalystSub, $"/api/v1/cases/{caseId}/messages");
        var fromBank = analystView.Should().ContainSingle().Subject;
        fromBank.SenderType.Should().Be(MessageSenderTypes.BankUser);
        fromBank.SenderId.Should().Be(own.BankUserId);
        fromBank.Body.Should().Be("Where is my refund?");

        var reply = await Post(own.AnalystSub, $"/api/v1/cases/{caseId}/messages", """{"body":"We have filed the dispute."}""");
        reply.StatusCode.Should().Be(HttpStatusCode.Created, await reply.Content.ReadAsStringAsync());

        var bankView = await GetOk<List<PortalMessageDto>>(own.BankUserSub, $"/api/v1/portal/cases/{caseId}/messages");
        bankView.Select(m => (m.SenderType, m.Body)).Should().Equal(
            (MessageSenderTypes.BankUser, "Where is my refund?"), (MessageSenderTypes.Analyst, "We have filed the dispute."));
        var raw = JsonDocument.Parse(await (await Get(own.BankUserSub, $"/api/v1/portal/cases/{caseId}/messages")).Content.ReadAsStringAsync()).RootElement;
        raw[1].TryGetProperty("senderId", out _).Should().BeFalse("the bank does not see analyst identities");

        (await Count("SELECT count(*) FROM chargeback_diagram.domain_events WHERE case_id = @id AND event_type = 'case.message.posted'", caseId)).Should().Be(2);

        // Scope, user type and validation.
        (await Post(other.BankUserSub, $"/api/v1/portal/cases/{caseId}/messages", """{"body":"x"}""")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Get(other.BankUserSub, $"/api/v1/portal/cases/{caseId}/messages")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Get(other.AnalystSub, $"/api/v1/cases/{caseId}/messages")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Get(own.BankUserSub, $"/api/v1/cases/{caseId}/messages")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Post(own.BankUserSub, $"/api/v1/portal/cases/{caseId}/messages", JsonSerializer.Serialize(new { body = new string('m', 2001) }))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await Post(own.BankUserSub, $"/api/v1/portal/cases/{caseId}/messages", """{"body":"card 4111111111111111"}""")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var noViewCases = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewBankUsers);
        await fixture.Data.GrantScopeAsync(noViewCases.Id, own.BankId);
        (await Post(noViewCases.Sub, $"/api/v1/cases/{caseId}/messages", """{"body":"x"}""")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Append-only.
        var update = () => fixture.Data.ExecuteAsync("UPDATE chargeback_diagram.portal_messages SET message_text = 'edited' WHERE case_id = @caseId", new { caseId });
        var delete = () => fixture.Data.ExecuteAsync("DELETE FROM chargeback_diagram.portal_messages WHERE case_id = @caseId", new { caseId });
        await update.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");
        await delete.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");
    }

    [Fact]
    public async Task Support_ticket_stub_accepts_and_records_the_payload()
    {
        var own = await PortalWorld();
        var other = await PortalWorld();
        var caseId = own.CaseIds[0];

        var response = await Post(own.BankUserSub, $"/api/v1/portal/cases/{caseId}/support-ticket", """{"subject":"Refund status","body":"Please call me back."}""");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var ticket = (await response.Content.ReadFromJsonAsync<SupportTicketAcceptedDto>(CurrentUserTests.Json))!;
        ticket.TicketId.Should().StartWith("STUB-");
        var recorded = JsonDocument.Parse(await fixture.Data.QueryScalarAsync<string>(
            "SELECT (event_data->'data')::text FROM chargeback_diagram.domain_events WHERE case_id = @caseId AND event_type = 'case.support.requested'",
            new { caseId })).RootElement;
        recorded.GetProperty("ticketId").GetString().Should().Be(ticket.TicketId);
        recorded.GetProperty("subject").GetString().Should().Be("Refund status");
        recorded.GetProperty("body").GetString().Should().Be("Please call me back.");

        (await Post(other.BankUserSub, $"/api/v1/portal/cases/{caseId}/support-ticket", """{"subject":"x","body":"y"}""")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Post(own.BankUserSub, $"/api/v1/portal/cases/{caseId}/support-ticket", """{"subject":"","body":"y"}""")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Migration_0009_is_idempotent()
    {
        var database = "mig9_" + Guid.NewGuid().ToString("N")[..10];
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.ExecuteAsync($"CREATE DATABASE {database}");
        }

        var target = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        await BaselineDatabase.ApplyAllAsync(target);
        await BaselineDatabase.ExecuteScriptAsync(target, BaselineDatabase.MigrationPath("0009_portal_messages.sql"));
        await BaselineDatabase.ExecuteScriptAsync(target, BaselineDatabase.MigrationPath("0009_portal_messages.sql"));

        await using var connection = new NpgsqlConnection(target);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM pg_trigger WHERE tgname = 'portal_messages_append_only' AND NOT tgisinternal")).Should().Be(1);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM pg_constraint WHERE conname = 'portal_messages_text_check'")).Should().Be(1);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private sealed record World(Guid BankId, string BankUserSub, Guid BankUserId, string AnalystSub, Guid[] CaseIds);

    /// <summary>A bank with two FLAGGED cases (GBP 80.00, SYNTHETIC merchant), a bank user and a scoped analyst.</summary>
    private async Task<World> PortalWorld()
    {
        var bankId = await fixture.Data.CreateBankAsync();
        var bankUser = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId);
        var analyst = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases);
        await fixture.Data.GrantScopeAsync(analyst.Id, bankId);
        var cases = new Guid[2];
        for (var i = 0; i < cases.Length; i++)
        {
            var disputeId = await fixture.Data.QueryScalarAsync<Guid>(
                """
                INSERT INTO chargeback_diagram.disputes(bank_id, transaction_amount, currency_code, merchant_name, intake_channel, status)
                VALUES (@bankId, 80.00, 'GBP', 'SYN merchant', 'PORTAL', 'FLAGGED') RETURNING id
                """,
                new { bankId });
            cases[i] = await fixture.Data.QueryScalarAsync<Guid>(
                "INSERT INTO chargeback_diagram.cases(dispute_id, case_reference, status, created_at) VALUES (@disputeId, @reference, 'FLAGGED', now() - make_interval(mins => @i)) RETURNING id",
                new { disputeId, reference = "CB-2026-" + Random.Shared.Next(100000, 999999).ToString(System.Globalization.CultureInfo.InvariantCulture) + i, i });
        }

        return new World(bankId, bankUser.Sub, bankUser.Id, analyst.Sub, cases);
    }

    private Task<long> Count(string sql, Guid id) => fixture.Data.QueryScalarAsync<long>(sql, new { id });

    private async Task<HttpResponseMessage> Get(string sub, string url)
    {
        using var client = fixture.Factory.CreateClientFor(sub);
        return await client.GetAsync(url);
    }

    private async Task<HttpResponseMessage> Post(string sub, string url, string body)
    {
        using var client = fixture.Factory.CreateClientFor(sub);
        return await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
    }

    private async Task<T> GetOk<T>(string sub, string url)
    {
        var response = await Get(sub, url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(CurrentUserTests.Json))!;
    }
}
