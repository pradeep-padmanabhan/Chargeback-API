using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.Infrastructure.Ai.Capabilities;

// Typed contracts for the six diagram capabilities (proposal for AI-team review; see
// docs/contracts/ai-capabilities.md). Inputs carry masked data only. Outputs are advisory:
// none of these types can express a reason-code choice, a case state change, a filing or a refund.

// ---- Extraction agent -------------------------------------------------------------------

public sealed record EmailParseInput(Guid BankId, string Subject, string Body, IReadOnlyList<string> AttachmentFileNames);

/// <summary>Candidate intake facts; each field may be absent. Low confidence requires human review.</summary>
public sealed record EmailParseOutput(
    AdvisoryField<string>? CardholderReference,
    AdvisoryField<string>? CardNumberLast4,
    AdvisoryField<DateTimeOffset>? TransactionDate,
    AdvisoryField<decimal>? TransactionAmount,
    AdvisoryField<string>? CurrencyCode,
    AdvisoryField<string>? AcquirerReferenceNumber,
    AdvisoryField<string>? MerchantName);

public interface IEmailParser
{
    Task<AiResult<EmailParseOutput>> ParseAsync(EmailParseInput input, CancellationToken cancellationToken);
}

public sealed record SdkConversationTurn(string Role, string Text);

public sealed record SdkIntakeGuideInput(Guid BankId, Guid SessionId, IReadOnlyList<SdkConversationTurn> History, string CardholderMessage);

/// <summary>Next question for the cardholder plus the structured draft so far. Eligibility is decided server-side, deterministically.</summary>
public sealed record SdkIntakeGuideOutput(string AssistantMessage, IReadOnlyDictionary<string, string> DraftFields);

public interface ISdkIntakeGuide
{
    Task<AiResult<SdkIntakeGuideOutput>> NextTurnAsync(SdkIntakeGuideInput input, CancellationToken cancellationToken);
}

// ---- Classification agent ---------------------------------------------------------------

public sealed record AttachmentCandidate(Guid AttachmentId, string FileName, string? ExtractedTextExcerpt);

public sealed record SlotCandidate(Guid DocumentSlotId, string SlotName, string? ExpectedType);

public sealed record AttachmentMatchInput(Guid CaseId, Guid BankId, IReadOnlyList<AttachmentCandidate> Attachments, IReadOnlyList<SlotCandidate> Slots);

public sealed record AttachmentMatch(Guid AttachmentId, Guid? SuggestedDocumentSlotId, decimal Confidence);

public sealed record AttachmentMatchOutput(IReadOnlyList<AttachmentMatch> Matches);

public interface IAttachmentMatcher
{
    Task<AiResult<AttachmentMatchOutput>> MatchAsync(AttachmentMatchInput input, CancellationToken cancellationToken);
}

public sealed record DocumentVerificationInput(Guid DocumentId, Guid CaseId, Guid BankId, string? ExpectedType, DocumentStage SchemeStage, string ExtractedText);

/// <summary>Maps to documents.ai_classification / ai_confidence. A human may override.</summary>
public sealed record DocumentVerificationOutput(string SuggestedType, decimal Confidence, IReadOnlyList<string> Concerns);

public interface IDocumentVerifier
{
    Task<AiResult<DocumentVerificationOutput>> VerifyAsync(DocumentVerificationInput input, CancellationToken cancellationToken);
}

// ---- Analysis & Explanation agent -------------------------------------------------------

/// <summary>The existing deterministic result to be explained; the model never produces these values.</summary>
public sealed record TriageSummaryInput(
    Guid CaseId,
    Guid BankId,
    string ReasonCode,
    DateOnly? FilingDeadlineDate,
    TriageOutcome Outcome,
    IReadOnlyList<GateResult> Gates,
    IReadOnlyList<string> RequiredDocuments);

public sealed record TriageSummaryOutput(string SummaryText);

public interface ITriageSummarizer
{
    Task<AiResult<TriageSummaryOutput>> SummarizeAsync(TriageSummaryInput input, CancellationToken cancellationToken);
}

public sealed record EvidenceDocument(Guid DocumentId, string? ClassifiedType, string? ExtractedTextExcerpt);

public sealed record EvidenceAnalysisInput(Guid CaseId, Guid BankId, string ReasonCode, IReadOnlyList<EvidenceDocument> Documents);

public sealed record EvidenceAnalysisOutput(string StrengthAssessment, IReadOnlyList<string> Gaps, string Rationale);

public interface IEvidenceAnalyzer
{
    Task<AiResult<EvidenceAnalysisOutput>> AnalyzeAsync(EvidenceAnalysisInput input, CancellationToken cancellationToken);
}
