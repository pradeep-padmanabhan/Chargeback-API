using Chargeback.Api.Features.Documents;
using Chargeback.Api.Features.Documents.Contracts;
using Chargeback.Api.Features.Documents.Upload;
using Chargeback.Infrastructure.Ai;
using Chargeback.Infrastructure.Storage;
using Chargeback.SharedKernel.ValueObjects;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Chargeback.UnitTests.Documents;

public sealed class DocumentLifecycleTests
{
    private const string Pending = DocumentUploadStatuses.PendingUpload;
    private const string Uploaded = DocumentUploadStatuses.Uploaded;

    [Theory]
    [InlineData(Pending, false, true)]
    [InlineData(Uploaded, false, true)]   // idempotent re-confirmation
    [InlineData(Pending, true, false)]
    [InlineData("BOGUS", false, false)]
    public void Confirm_guard(string upload, bool deleted, bool allowed) =>
        DocumentLifecycle.CanConfirmUpload(upload, deleted).Should().Be(allowed);

    [Theory]
    [InlineData(Pending, DocumentStatus.Pending, false, true)]
    [InlineData(Uploaded, DocumentStatus.Pending, false, true)]      // confirmed, processing not started
    [InlineData(Uploaded, DocumentStatus.Processing, false, false)]  // processing started
    [InlineData(Uploaded, DocumentStatus.Success, false, false)]
    [InlineData(Uploaded, DocumentStatus.Failed, false, false)]
    [InlineData(Pending, DocumentStatus.Pending, true, false)]       // already deleted
    public void Delete_guard(string upload, DocumentStatus processing, bool deleted, bool allowed) =>
        DocumentLifecycle.CanDelete(upload, processing, deleted).Should().Be(allowed);

    [Theory]
    [InlineData(Uploaded, DocumentStatus.Pending, false, true)]
    [InlineData(Pending, DocumentStatus.Pending, false, false)]      // not confirmed
    [InlineData(Uploaded, DocumentStatus.Processing, false, false)]  // already claimed
    [InlineData(Uploaded, DocumentStatus.Failed, false, false)]      // no retry
    [InlineData(Uploaded, DocumentStatus.Pending, true, false)]      // deleted
    public void Processing_guard(string upload, DocumentStatus processing, bool deleted, bool allowed) =>
        DocumentLifecycle.CanStartProcessing(upload, processing, deleted).Should().Be(allowed);

    [Theory]
    [InlineData(new string[0], SlotFulfillment.Missing)]
    [InlineData(new[] { Pending }, SlotFulfillment.AwaitingUpload)]
    [InlineData(new[] { Pending, Pending }, SlotFulfillment.AwaitingUpload)]
    [InlineData(new[] { Pending, Uploaded }, SlotFulfillment.Uploaded)]
    [InlineData(new[] { Uploaded }, SlotFulfillment.Uploaded)]
    public void Slot_fulfillment_counts_live_uploads_only(string[] liveStatuses, SlotFulfillment expected) =>
        DocumentLifecycle.Fulfillment(liveStatuses).Should().Be(expected);

    [Theory]
    [InlineData(AiResultStatus.Unavailable, "AI_UNAVAILABLE")]
    [InlineData(AiResultStatus.Disabled, "AI_DISABLED")]
    [InlineData(AiResultStatus.InvalidOutput, "AI_INVALID_OUTPUT")]
    public void Failure_reasons(AiResultStatus status, string reason) =>
        DocumentLifecycle.FailureReason(status).Should().Be(reason);

    [Fact]
    public void S3_key_is_built_from_ids_only()
    {
        var caseId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        DocumentLifecycle.S3Key(caseId, documentId).Should().Be($"cases/{caseId:N}/documents/{documentId:N}");
    }
}

public sealed class CreateDocumentUploadValidatorTests
{
    private static readonly CreateDocumentUploadValidator Validator = new(Options.Create(new DocumentOptions()));

    [Theory]
    [InlineData("receipt.pdf", "application/pdf", 1L, true)]
    [InlineData("photo.jpg", "image/jpeg", 26_214_400L, true)]          // exactly 25 MB
    [InlineData("scan.png", "IMAGE/PNG", 10L, true)]                    // case-insensitive MIME
    [InlineData("scan.tif", "image/tiff", 10L, true)]
    [InlineData("anim.gif", "image/gif", 10L, false)]
    [InlineData("doc.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", 10L, false)]
    [InlineData("big.pdf", "application/pdf", 26_214_401L, false)]      // over 25 MB
    [InlineData("empty.pdf", "application/pdf", 0L, false)]
    [InlineData("dir/receipt.pdf", "application/pdf", 10L, false)]
    [InlineData("dir\\receipt.pdf", "application/pdf", 10L, false)]
    [InlineData("tab\there.pdf", "application/pdf", 10L, false)]
    [InlineData("4111111111111111.pdf", "application/pdf", 10L, false)]
    [InlineData("", "application/pdf", 10L, false)]
    public void Upload_declaration_shape(string fileName, string mimeType, long size, bool valid) =>
        Validator.Validate(new CreateDocumentUploadCommand(Guid.NewGuid(), new CreateDocumentUploadRequest(null, fileName, mimeType, size, DocumentStage.Initial)))
            .IsValid.Should().Be(valid);

    [Fact]
    public void Scheme_stage_is_required() =>
        Validator.Validate(new CreateDocumentUploadCommand(Guid.NewGuid(), new CreateDocumentUploadRequest(null, "a.pdf", "application/pdf", 10, null)))
            .IsValid.Should().BeFalse();

    [Fact]
    public void Maximum_size_is_configurable()
    {
        var strict = new CreateDocumentUploadValidator(Options.Create(new DocumentOptions { MaxFileSizeBytes = 100 }));

        strict.Validate(new CreateDocumentUploadCommand(Guid.NewGuid(), new CreateDocumentUploadRequest(null, "a.pdf", "application/pdf", 101, DocumentStage.Initial)))
            .IsValid.Should().BeFalse();
    }
}

public sealed class KnownLimitationS3ServiceTests
{
    [Fact]
    public async Task Issues_a_distinct_short_lived_url_bound_to_type_and_length()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));
        var s3 = new KnownLimitationS3Service(time);

        var first = await s3.CreatePresignedPutAsync("cases/a/documents/b", "application/pdf", 2048, TimeSpan.FromMinutes(15), CancellationToken.None);
        var second = await s3.CreatePresignedPutAsync("cases/a/documents/b", "application/pdf", 2048, TimeSpan.FromMinutes(15), CancellationToken.None);

        first.Url.Should().NotBe(second.Url);
        first.Url.Host.Should().Be(KnownLimitationS3Service.StubHost);
        first.Url.AbsolutePath.Should().Be("/cases/a/documents/b");
        first.Url.Query.Should().Contain("X-Amz-Expires=900").And.Contain(KnownLimitationS3Service.Marker);
        first.ExpiresAt.Should().Be(time.GetUtcNow().AddMinutes(15));
        first.RequiredHeaders.Should().Contain("Content-Type", "application/pdf").And.Contain("Content-Length", "2048");
    }
}
