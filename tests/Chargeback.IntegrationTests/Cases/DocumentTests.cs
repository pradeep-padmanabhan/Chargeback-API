using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chargeback.Api.Common.Http;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Cases.Contracts;
using Chargeback.Api.Features.Documents.Contracts;
using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Infrastructure.Storage;
using Chargeback.IntegrationTests.Security;
using Chargeback.SharedKernel.ValueObjects;
using Chargeback.TestSupport;

namespace Chargeback.IntegrationTests.Cases;

/// <summary>
/// Evidence &amp; Documents end to end with the KNOWN_LIMITATION_S3_ stub: declare → (client uploads to S3) → confirm →
/// asynchronous classification → polling. The default host's Document Verification capability is the production
/// (unavailable) one; the <c>Synthetic</c> host uses a SYNTHETIC verifier.
/// </summary>
[Collection(CaseCollection.Name)]
public sealed class DocumentTests(CaseFixture fixture)
{
    [Fact]
    public async Task Declare_confirm_and_poll_records_ai_unavailable_without_retry()
    {
        var (world, caseId) = await NewCase();

        var declared = await Declare(world.AnalystSub, caseId, Upload("receipt.pdf", "application/pdf", 2048, "Initial"));
        declared.StatusCode.Should().Be(HttpStatusCode.Created, await declared.Content.ReadAsStringAsync());
        var ticket = (await declared.Content.ReadFromJsonAsync<DocumentUploadTicketDto>(CurrentUserTests.Json))!;
        ticket.UploadUrl.Host.Should().Be(KnownLimitationS3Service.StubHost);
        ticket.UploadUrl.Query.Should().Contain(KnownLimitationS3Service.Marker);
        ticket.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromMinutes(1));
        ticket.RequiredHeaders.Should().Contain("Content-Type", "application/pdf").And.Contain("Content-Length", "2048");
        ticket.Document.UploadStatus.Should().Be(DocumentUploadStatuses.PendingUpload);
        ticket.Document.ProcessingStatus.Should().Be(DocumentStatus.Pending);
        ticket.Document.SchemeStage.Should().Be(DocumentStage.Initial);
        ticket.Document.UploadedBy.Should().Be(world.AnalystId);

        var confirmed = await Confirm(world.AnalystSub, caseId, ticket.DocumentId);
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());
        var uploaded = (await confirmed.Content.ReadFromJsonAsync<DocumentDto>(CurrentUserTests.Json))!;
        uploaded.UploadStatus.Should().Be(DocumentUploadStatuses.Uploaded);
        uploaded.UploadConfirmedAt.Should().NotBeNull();
        uploaded.ProcessingStatus.Should().Be(DocumentStatus.Pending, "classification runs asynchronously");

        await CaseFixture.DrainOutboxAsync(fixture.Default);

        var polled = await Get<DocumentDto>(world.AnalystSub, $"/api/v1/cases/{caseId}/documents/{ticket.DocumentId}");
        polled.UploadStatus.Should().Be(DocumentUploadStatuses.Uploaded, "upload and processing statuses are independent");
        polled.ProcessingStatus.Should().Be(DocumentStatus.Failed);
        polled.ProcessingFailureReason.Should().Be("AI_UNAVAILABLE");
        polled.Classification!.Status.Should().Be("FAILED");
        polled.Classification.FailureReason.Should().Be("AI_UNAVAILABLE");
        polled.Classification.Advisory.Should().BeTrue();

        // Confirming again is a no-op: no second event, no second classification run.
        (await Confirm(world.AnalystSub, caseId, ticket.DocumentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        await CaseFixture.DrainOutboxAsync(fixture.Default);
        (await Count("SELECT count(*) FROM chargeback_diagram.document_classifications WHERE document_id = @id", ticket.DocumentId)).Should().Be(1);

        var timeline = await Get<List<CaseTimelineEntryDto>>(world.AnalystSub, $"/api/v1/cases/{caseId}/timeline");
        timeline.Select(e => e.EventType).Where(t => t.StartsWith("document.", StringComparison.Ordinal))
            .Should().Equal("document.upload.requested", "document.uploaded", "document.processed");
    }

    [Fact]
    public async Task Successful_classification_lands_in_its_own_table_and_slots_report_fulfillment()
    {
        var (world, caseId) = await NewCase();
        var slotId = await fixture.Data.QueryScalarAsync<Guid>(
            "INSERT INTO chargeback_diagram.document_slots(case_id, slot_name, is_required, expected_type) VALUES (@caseId, 'SYN slot', true, 'SYN_TYPE') RETURNING id",
            new { caseId });
        (await Get<CaseDocumentsDto>(world.AnalystSub, $"/api/v1/cases/{caseId}/documents")).Slots.Should().ContainSingle()
            .Which.Fulfillment.Should().Be(SlotFulfillment.Missing);

        var ticket = await DeclareOk(world.AnalystSub, caseId, Upload("scan.tiff", "image/tiff", 4096, "PreArbitration", slotId));
        (await Get<CaseDocumentsDto>(world.AnalystSub, $"/api/v1/cases/{caseId}/documents")).Slots.Single().Fulfillment.Should().Be(SlotFulfillment.AwaitingUpload);

        (await Confirm(world.AnalystSub, caseId, ticket.DocumentId, fixture.Synthetic)).StatusCode.Should().Be(HttpStatusCode.OK);
        await CaseFixture.DrainOutboxAsync(fixture.Synthetic);

        fixture.Verifier.InputFor(ticket.DocumentId)!.ExpectedType.Should().Be("SYN_TYPE");
        fixture.Verifier.InputFor(ticket.DocumentId)!.SchemeStage.Should().Be(DocumentStage.PreArbitration);
        var documents = await Get<CaseDocumentsDto>(world.AnalystSub, $"/api/v1/cases/{caseId}/documents");
        var slot = documents.Slots.Single();
        slot.Fulfillment.Should().Be(SlotFulfillment.Uploaded);
        slot.Documents.Should().ContainSingle().Which.ProcessingStatus.Should().Be(DocumentStatus.Success);
        var document = documents.Documents.Should().ContainSingle().Subject;
        document.Classification!.Status.Should().Be("SUCCESS");
        document.Classification.Category.Should().Be(FakeDocumentVerifier.Category);
        document.Classification.Confidence.Should().Be(0.8750m);
        document.Classification.Concerns.Should().Equal("SYNTHETIC concern");
        document.Classification.ModelName.Should().Be(FakeDocumentVerifier.ModelName);
        document.ProcessingFailureReason.Should().BeNull();
    }

    [Fact]
    public async Task Declaration_is_validated_and_issues_a_new_url_every_time()
    {
        var (world, caseId) = await NewCase();
        var (_, otherCase) = await NewCase();
        var foreignSlot = await fixture.Data.QueryScalarAsync<Guid>(
            "INSERT INTO chargeback_diagram.document_slots(case_id, slot_name) VALUES (@otherCase, 'SYN other') RETURNING id", new { otherCase });

        foreach (var invalid in new[]
        {
            Upload("a.gif", "image/gif", 10, "Initial"),
            Upload("a.pdf", "application/pdf", 0, "Initial"),
            Upload("a.pdf", "application/pdf", (25L * 1024 * 1024) + 1, "Initial"),
            Upload("../a.pdf", "application/pdf", 10, "Initial"),
            Upload("card 4111111111111111.pdf", "application/pdf", 10, "Initial"),
            Upload("a.pdf", "application/pdf", 10, null),
            Upload("", "application/pdf", 10, "Initial"),
        })
        {
            (await Declare(world.AnalystSub, caseId, invalid)).StatusCode.Should().Be(HttpStatusCode.BadRequest, invalid);
        }

        var wrongSlot = await Declare(world.AnalystSub, caseId, Upload("a.pdf", "application/pdf", 10, "Initial", foreignSlot));
        wrongSlot.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await wrongSlot.Content.ReadAsStringAsync()).Should().Contain("DOCUMENT_SLOT_NOT_IN_CASE");

        var first = await DeclareOk(world.AnalystSub, caseId, Upload("a.pdf", "APPLICATION/PDF", 25L * 1024 * 1024, "Arbitration"));
        var second = await DeclareOk(world.AnalystSub, caseId, Upload("a.pdf", "application/pdf", 10, "Initial"));
        first.UploadUrl.Should().NotBe(second.UploadUrl, "pre-signed URLs are never reused");
        first.Document.MimeType.Should().Be("application/pdf");
        first.UploadUrl.AbsolutePath.Should().NotContain("a.pdf", "the S3 key is built from ids only");
    }

    [Fact]
    public async Task Soft_delete_is_allowed_only_before_processing_starts()
    {
        var (world, caseId) = await NewCase();

        // PENDING_UPLOAD → deletable; the row stays with deletedAt and drops out of the list.
        var pending = await DeclareOk(world.AnalystSub, caseId, Upload("a.pdf", "application/pdf", 10, "Initial"));
        (await Delete(world.AnalystSub, caseId, pending.DocumentId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Get<CaseDocumentsDto>(world.AnalystSub, $"/api/v1/cases/{caseId}/documents")).Documents.Should().NotContain(d => d.Id == pending.DocumentId);
        (await Get<DocumentDto>(world.AnalystSub, $"/api/v1/cases/{caseId}/documents/{pending.DocumentId}")).DeletedAt.Should().NotBeNull();
        (await Delete(world.AnalystSub, caseId, pending.DocumentId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Confirm(world.AnalystSub, caseId, pending.DocumentId)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // UPLOADED but not yet processed → deletable, and the queued classification then skips it.
        var queued = await DeclareOk(world.AnalystSub, caseId, Upload("b.pdf", "application/pdf", 10, "Initial"));
        (await Confirm(world.AnalystSub, caseId, queued.DocumentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Delete(world.AnalystSub, caseId, queued.DocumentId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await CaseFixture.DrainOutboxAsync(fixture.Default);
        (await Get<DocumentDto>(world.AnalystSub, $"/api/v1/cases/{caseId}/documents/{queued.DocumentId}")).ProcessingStatus.Should().Be(DocumentStatus.Pending);
        (await Count("SELECT count(*) FROM chargeback_diagram.document_classifications WHERE document_id = @id", queued.DocumentId)).Should().Be(0);

        // Processed → not deletable.
        var processed = await DeclareOk(world.AnalystSub, caseId, Upload("c.pdf", "application/pdf", 10, "Initial"));
        (await Confirm(world.AnalystSub, caseId, processed.DocumentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        await CaseFixture.DrainOutboxAsync(fixture.Default);
        var refused = await Delete(world.AnalystSub, caseId, processed.DocumentId);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("DOCUMENT_NOT_DELETABLE");
    }

    [Fact]
    public async Task Documents_are_permission_and_scope_protected()
    {
        var (world, caseId) = await NewCase();
        var ticket = await DeclareOk(world.AnalystSub, caseId, Upload("a.pdf", "application/pdf", 10, "Initial"));
        var viewOnly = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases, Permissions.ViewDocuments);
        var uploadOnly = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases, Permissions.UploadDocument);
        await fixture.Data.GrantScopeAsync(viewOnly.Id, world.BankId);
        await fixture.Data.GrantScopeAsync(uploadOnly.Id, world.BankId);
        var (otherWorld, otherCase) = await NewCase();
        var body = Upload("a.pdf", "application/pdf", 10, "Initial");

        (await Declare(viewOnly.Sub, caseId, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Confirm(viewOnly.Sub, caseId, ticket.DocumentId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Delete(viewOnly.Sub, caseId, ticket.DocumentId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using (var client = fixture.Default.CreateClientFor(uploadOnly.Sub))
        {
            (await client.GetAsync($"/api/v1/cases/{caseId}/documents")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await client.GetAsync($"/api/v1/cases/{caseId}/documents/{ticket.DocumentId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await Declare(world.BankUserSub, caseId, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "bank-user upload is an open decision");
        (await Declare(otherWorld.AnalystSub, caseId, body)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var client = fixture.Default.CreateClientFor(otherWorld.AnalystSub))
        {
            (await client.GetAsync($"/api/v1/cases/{caseId}/documents/{ticket.DocumentId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

            // A document is only reachable under its own case, even within the caller's scope.
            (await client.GetAsync($"/api/v1/cases/{otherCase}/documents/{ticket.DocumentId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Upload_stage_fields_and_classification_runs_are_immutable()
    {
        var (world, caseId) = await NewCase();
        var ticket = await DeclareOk(world.AnalystSub, caseId, Upload("a.pdf", "application/pdf", 10, "Initial"));
        (await Confirm(world.AnalystSub, caseId, ticket.DocumentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        await CaseFixture.DrainOutboxAsync(fixture.Default);
        var id = ticket.DocumentId;

        foreach (var sql in new[]
        {
            "UPDATE chargeback_diagram.documents SET file_name = 'renamed.pdf' WHERE id = @id",
            "UPDATE chargeback_diagram.documents SET s3_key = 'elsewhere' WHERE id = @id",
            "UPDATE chargeback_diagram.documents SET mime_type = 'image/png' WHERE id = @id",
            "UPDATE chargeback_diagram.documents SET file_size_bytes = 99 WHERE id = @id",
            "UPDATE chargeback_diagram.documents SET uploaded_at = now() WHERE id = @id",
            "UPDATE chargeback_diagram.documents SET scheme_stage = 'Arbitration' WHERE id = @id",
            "UPDATE chargeback_diagram.documents SET upload_status = 'PENDING_UPLOAD' WHERE id = @id",
            "DELETE FROM chargeback_diagram.documents WHERE id = @id",
            "UPDATE chargeback_diagram.document_classifications SET category = 'X' WHERE document_id = @id",
            "DELETE FROM chargeback_diagram.document_classifications WHERE document_id = @id",
        })
        {
            var act = () => fixture.Data.ExecuteAsync(sql, new { id });
            await act.Should().ThrowAsync<Npgsql.PostgresException>(sql);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private sealed record TestWorld(Guid BankId, string BankUserSub, string AnalystSub, Guid AnalystId);

    private async Task<(TestWorld World, Guid CaseId)> NewCase()
    {
        var bankId = await fixture.Data.CreateBankAsync();
        var bankUser = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId, Permissions.CreateDispute, Permissions.ViewCases);
        var analyst = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null,
            Permissions.ViewCases, Permissions.UploadDocument, Permissions.ViewDocuments);
        await fixture.Data.GrantScopeAsync(analyst.Id, bankId);
        var world = new TestWorld(bankId, bankUser.Sub, analyst.Sub, analyst.Id);

        using var client = fixture.Default.CreateClientFor(bankUser.Sub);
        client.DefaultRequestHeaders.Add(IdempotencyKey.HeaderName, "idem-" + Guid.NewGuid().ToString("N"));
        var response = await client.PostAsync("/api/v1/intake/disputes", new StringContent(
            $$"""{"bankId":"{{bankId}}","transactionDate":"2026-09-01T10:00:00Z","transactionAmount":80.00,"currencyCode":"GBP","merchantName":"SYN merchant"}""",
            Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var disputeId = (await response.Content.ReadFromJsonAsync<DisputeAcceptedResponse>(CurrentUserTests.Json))!.DisputeId;
        await CaseFixture.DrainOutboxAsync(fixture.Default);
        return (world, await fixture.Data.QueryScalarAsync<Guid>("SELECT id FROM chargeback_diagram.cases WHERE dispute_id = @disputeId", new { disputeId }));
    }

    private static string Upload(string fileName, string mimeType, long size, string? stage, Guid? slotId = null) =>
        JsonSerializer.Serialize(new { documentSlotId = slotId, fileName, mimeType, fileSizeBytes = size, schemeStage = stage });

    private async Task<HttpResponseMessage> Declare(string sub, Guid caseId, string body)
    {
        using var client = fixture.Default.CreateClientFor(sub);
        return await client.PostAsync($"/api/v1/cases/{caseId}/documents", new StringContent(body, Encoding.UTF8, "application/json"));
    }

    private async Task<DocumentUploadTicketDto> DeclareOk(string sub, Guid caseId, string body)
    {
        var response = await Declare(sub, caseId, body);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DocumentUploadTicketDto>(CurrentUserTests.Json))!;
    }

    private async Task<HttpResponseMessage> Confirm(string sub, Guid caseId, Guid documentId, ChargebackApiFactory? host = null)
    {
        using var client = (host ?? fixture.Default).CreateClientFor(sub);
        return await client.PostAsync($"/api/v1/cases/{caseId}/documents/{documentId}/uploaded", null);
    }

    private async Task<HttpResponseMessage> Delete(string sub, Guid caseId, Guid documentId)
    {
        using var client = fixture.Default.CreateClientFor(sub);
        return await client.DeleteAsync($"/api/v1/cases/{caseId}/documents/{documentId}");
    }

    private Task<long> Count(string sql, Guid id) => fixture.Data.QueryScalarAsync<long>(sql, new { id });

    private async Task<T> Get<T>(string sub, string url)
    {
        using var client = fixture.Default.CreateClientFor(sub);
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(CurrentUserTests.Json))!;
    }
}
