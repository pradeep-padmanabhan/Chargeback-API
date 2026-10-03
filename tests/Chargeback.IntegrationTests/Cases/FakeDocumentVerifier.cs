using System.Collections.Concurrent;
using Chargeback.Infrastructure.Ai;
using Chargeback.Infrastructure.Ai.Capabilities;

namespace Chargeback.IntegrationTests.Cases;

/// <summary>SYNTHETIC Document Verification capability: always classifies as <see cref="Category"/>. Records inputs per document.</summary>
public sealed class FakeDocumentVerifier : IDocumentVerifier
{
    public const string Category = "SYNTHETIC_RECEIPT";
    public const string ModelName = "synthetic-verifier";

    private readonly ConcurrentDictionary<Guid, DocumentVerificationInput> _inputs = new();

    public DocumentVerificationInput? InputFor(Guid documentId) => _inputs.TryGetValue(documentId, out var input) ? input : null;

    public Task<AiResult<DocumentVerificationOutput>> VerifyAsync(DocumentVerificationInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        _inputs[input.DocumentId] = input;
        return Task.FromResult(AiResult<DocumentVerificationOutput>.Success(
            new DocumentVerificationOutput(Category, 0.8750m, ["SYNTHETIC concern"]), ModelName));
    }
}
