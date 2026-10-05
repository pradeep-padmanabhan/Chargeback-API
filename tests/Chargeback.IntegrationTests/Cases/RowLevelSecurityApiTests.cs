using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chargeback.Api.Common.Http;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Cases.Contracts;
using Chargeback.Api.Features.ClientPortal.Contracts;
using Chargeback.Api.Features.Documents.Contracts;
using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Infrastructure.Outbox;
using Chargeback.IntegrationTests.Persistence;
using Chargeback.IntegrationTests.Security;
using Chargeback.SharedKernel.Paging;
using Chargeback.SharedKernel.ValueObjects;
using Chargeback.TestSupport;

namespace Chargeback.IntegrationTests.Cases;

/// <summary>
/// The API end to end with row-level security ENFORCED: the host connects as a non-superuser login in chargeback_app,
/// so every query is filtered by the policies of migration 0010. Proves the scope is established on every path
/// (queries, transactional and non-transactional commands, authorization's resource lookup, workflow consumers).
/// </summary>
[Collection(CaseCollection.Name)]
public sealed class RowLevelSecurityApiTests(CaseFixture fixture) : IAsyncLifetime
{
    private ChargebackApiFactory _host = null!;

    public async Task InitializeAsync() => _host = new ChargebackApiFactory(await RlsLogin.EnsureAsync(fixture.ConnectionString))
    {
        Settings = new Dictionary<string, string>
        {
            ["Outbox:Transport"] = OutboxTransports.InProcess,
            ["Outbox:DispatcherEnabled"] = "false",
            ["RowLevelSecurity:RequireEnforcedLogin"] = "true",
        },
    };

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Full_case_journey_works_under_enforced_rls_and_other_banks_stay_invisible()
    {
        var bankA = await fixture.Data.CreateBankAsync();
        var bankB = await fixture.Data.CreateBankAsync();
        var bankUser = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankA, Permissions.CreateDispute);
        var analyst = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null,
            Permissions.ViewCases, Permissions.UpdateCaseStatus, Permissions.ReviewCase, Permissions.UploadDocument, Permissions.ViewDocuments);
        await fixture.Data.GrantScopeAsync(analyst.Id, bankA);
        var otherAnalyst = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases);
        await fixture.Data.GrantScopeAsync(otherAnalyst.Id, bankB);

        // Intake (bank scope, non-transactional command) and case creation (workflow consumer, system scope).
        var submitted = await Send(bankUser.Sub, HttpMethod.Post, "/api/v1/intake/disputes",
            $$"""{"bankId":"{{bankA}}","transactionAmount":80.00,"currencyCode":"GBP","merchantName":"SYN merchant"}""", idempotent: true);
        submitted.StatusCode.Should().Be(HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync());
        var disputeId = (await submitted.Content.ReadFromJsonAsync<DisputeAcceptedResponse>(CurrentUserTests.Json))!.DisputeId;
        await CaseFixture.DrainOutboxAsync(_host);
        var caseId = await fixture.Data.QueryScalarAsync<Guid>("SELECT id FROM chargeback_diagram.cases WHERE dispute_id = @disputeId", new { disputeId });

        // Queries: scope-filtered list, resource-scoped detail (authorization's lookup runs in system scope).
        (await GetOk<PagedResult<CaseSummaryDto>>(analyst.Sub, $"/api/v1/cases?bankId={bankA}")).Items.Should().ContainSingle(c => c.Id == caseId);
        var detail = await GetOk<CaseDetailDto>(analyst.Sub, $"/api/v1/cases/{caseId}");
        (await Send(otherAnalyst.Sub, HttpMethod.Get, $"/api/v1/cases/{caseId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetOk<PagedResult<CaseSummaryDto>>(otherAnalyst.Sub, "/api/v1/cases?pageSize=100")).Items.Should().NotContain(c => c.Id == caseId);

        // Transactional command + its consumer (system scope).
        (await Send(analyst.Sub, HttpMethod.Post, $"/api/v1/cases/{caseId}/transitions",
            JsonSerializer.Serialize(new { action = "START_REVIEW", expectedVersion = detail.Version }), idempotent: true)).StatusCode.Should().Be(HttpStatusCode.OK);
        await CaseFixture.DrainOutboxAsync(_host);

        // Documents: non-transactional commands and the classification consumer.
        var declared = await Send(analyst.Sub, HttpMethod.Post, $"/api/v1/cases/{caseId}/documents",
            """{"fileName":"receipt.pdf","mimeType":"application/pdf","fileSizeBytes":10,"schemeStage":"Initial"}""");
        declared.StatusCode.Should().Be(HttpStatusCode.Created, await declared.Content.ReadAsStringAsync());
        var documentId = (await declared.Content.ReadFromJsonAsync<DocumentUploadTicketDto>(CurrentUserTests.Json))!.DocumentId;
        (await Send(analyst.Sub, HttpMethod.Post, $"/api/v1/cases/{caseId}/documents/{documentId}/uploaded")).StatusCode.Should().Be(HttpStatusCode.OK);
        await CaseFixture.DrainOutboxAsync(_host);
        (await GetOk<DocumentDto>(analyst.Sub, $"/api/v1/cases/{caseId}/documents/{documentId}")).ProcessingStatus.Should().Be(DocumentStatus.Failed);

        // Human Review decision.
        var version = (await GetOk<CaseDetailDto>(analyst.Sub, $"/api/v1/cases/{caseId}")).Version;
        (await Send(analyst.Sub, HttpMethod.Post, $"/api/v1/cases/{caseId}/review/decision",
            JsonSerializer.Serialize(new { decision = "Reject", rationale = "no basis", expectedVersion = version }), idempotent: true))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Client Portal and the thread.
        (await GetOk<PortalCaseDetailDto>(bankUser.Sub, $"/api/v1/portal/cases/{caseId}")).Status.Should().Be(CaseStatuses.Rejected);
        (await Send(bankUser.Sub, HttpMethod.Post, $"/api/v1/portal/cases/{caseId}/messages", """{"body":"Why was it rejected?"}""")).StatusCode
            .Should().Be(HttpStatusCode.Created);
        (await GetOk<List<CaseMessageDto>>(analyst.Sub, $"/api/v1/cases/{caseId}/messages")).Should().ContainSingle();

        // Proof the host's login really is filtered: without the app's scope the same login sees none of this case.
        await using var login = new Npgsql.NpgsqlConnection(await RlsLogin.EnsureAsync(fixture.ConnectionString));
        await login.OpenAsync();
        const string visible = "SELECT count(*) FROM chargeback_diagram.cases WHERE id = @caseId";
        (await Dapper.SqlMapper.ExecuteScalarAsync<long>(login, visible, new { caseId })).Should().Be(0, "no scope set");
        await Dapper.SqlMapper.ExecuteAsync(login, "SELECT set_config('app.scope', 'banks', false), set_config('app.bank_ids', @ids, false)", new { ids = "{" + bankB + "}" });
        (await Dapper.SqlMapper.ExecuteScalarAsync<long>(login, visible, new { caseId })).Should().Be(0, "another bank's scope");
        await Dapper.SqlMapper.ExecuteAsync(login, "SELECT set_config('app.bank_ids', @ids, false)", new { ids = "{" + bankA + "}" });
        (await Dapper.SqlMapper.ExecuteScalarAsync<long>(login, visible, new { caseId })).Should().Be(1, "the case's own bank");
    }

    [Fact]
    public async Task Readiness_is_healthy_only_when_the_api_login_is_subject_to_rls()
    {
        using (var enforced = _host.CreateClientFor(null))
        {
            var ready = await enforced.GetAsync("/health/ready");
            ready.StatusCode.Should().Be(HttpStatusCode.OK, await ready.Content.ReadAsStringAsync());
        }

        // The same check against the superuser login (RLS silently bypassed) must fail readiness (guide §8 #41).
        await using var superuser = new ChargebackApiFactory(fixture.ConnectionString)
        {
            Settings = new Dictionary<string, string> { ["RowLevelSecurity:RequireEnforcedLogin"] = "true" },
        };
        using var client = superuser.CreateClientFor(null);
        (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    private async Task<HttpResponseMessage> Send(string sub, HttpMethod method, string url, string? body = null, bool idempotent = false)
    {
        using var client = _host.CreateClientFor(sub);
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (idempotent)
        {
            request.Headers.Add(IdempotencyKey.HeaderName, "idem-" + Guid.NewGuid().ToString("N"));
        }

        return await client.SendAsync(request);
    }

    private async Task<T> GetOk<T>(string sub, string url)
    {
        var response = await Send(sub, HttpMethod.Get, url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(CurrentUserTests.Json))!;
    }
}
