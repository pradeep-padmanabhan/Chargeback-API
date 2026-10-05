namespace Chargeback.Infrastructure.Ai.Capabilities;

/// <summary>
/// Default registration for all six capabilities until prompts/adapters are approved:
/// every call fails soft with <see cref="AiResultStatus.Unavailable"/>, which is exactly the path
/// the workflow must already handle when Bedrock is down.
/// </summary>
internal sealed class UnavailableAiCapabilities :
    IEmailParser, ISdkIntakeGuide, IAttachmentMatcher, IDocumentVerifier, ITriageSummarizer, IEvidenceAnalyzer
{
    private const string Reason = "CAPABILITY_NOT_IMPLEMENTED";

    public Task<AiResult<EmailParseOutput>> ParseAsync(EmailParseInput input, CancellationToken cancellationToken) =>
        Unavailable<EmailParseOutput>();

    public Task<AiResult<SdkIntakeGuideOutput>> NextTurnAsync(SdkIntakeGuideInput input, CancellationToken cancellationToken) =>
        Unavailable<SdkIntakeGuideOutput>();

    public Task<AiResult<AttachmentMatchOutput>> MatchAsync(AttachmentMatchInput input, CancellationToken cancellationToken) =>
        Unavailable<AttachmentMatchOutput>();

    public Task<AiResult<DocumentVerificationOutput>> VerifyAsync(DocumentVerificationInput input, CancellationToken cancellationToken) =>
        Unavailable<DocumentVerificationOutput>();

    public Task<AiResult<TriageSummaryOutput>> SummarizeAsync(TriageSummaryInput input, CancellationToken cancellationToken) =>
        Unavailable<TriageSummaryOutput>();

    public Task<AiResult<EvidenceAnalysisOutput>> AnalyzeAsync(EvidenceAnalysisInput input, CancellationToken cancellationToken) =>
        Unavailable<EvidenceAnalysisOutput>();

    private static Task<AiResult<T>> Unavailable<T>()
        where T : class =>
        Task.FromResult(AiResult<T>.Failed(AiResultStatus.Unavailable, Reason));
}
