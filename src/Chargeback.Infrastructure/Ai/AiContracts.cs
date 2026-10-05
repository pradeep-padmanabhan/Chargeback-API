namespace Chargeback.Infrastructure.Ai;

/// <summary>The three bounded logical agents (common guide §4).</summary>
public static class AiAgents
{
    public const string Extraction = "Extraction";
    public const string Classification = "Classification";
    public const string AnalysisExplanation = "AnalysisExplanation";
}

/// <summary>The six diagram capabilities (common guide §4).</summary>
public static class AiCapabilities
{
    public const string EmailParser = "EmailParser";
    public const string SdkIntakeGuide = "SdkIntakeGuide";
    public const string AttachmentMatcher = "AttachmentMatcher";
    public const string DocumentVerification = "DocumentVerification";
    public const string TriageSummary = "TriageSummary";
    public const string EvidenceAnalysis = "EvidenceAnalysis";
}

/// <summary>
/// One model invocation. <see cref="Input"/> is masked again by the client before it leaves
/// the process; callers must still never place full PAN/CVV into it.
/// </summary>
public sealed record AiInvocation(
    string AgentName,
    string CapabilityName,
    string PromptTemplateId,
    string PromptTemplateVersion,
    string Input,
    Guid? CaseId,
    Guid? BankId);

public enum AiResultStatus
{
    Success,

    /// <summary>Capability switched off by feature flag for this environment/bank.</summary>
    Disabled,

    /// <summary>Timeout, transport failure, not configured or audit failure. Fail-soft: the workflow continues without AI.</summary>
    Unavailable,

    /// <summary>The model output did not satisfy the capability's schema; it is discarded.</summary>
    InvalidOutput,
}

/// <summary>Advisory AI output. Nothing in this type can express a decision, state change, filing or refund.</summary>
public sealed record AiResult<T>(AiResultStatus Status, T? Output, string? ModelName, string? FailureReason)
    where T : class
{
    public bool IsSuccess => Status == AiResultStatus.Success && Output is not null;

    public static AiResult<T> Success(T output, string modelName) => new(AiResultStatus.Success, output, modelName, null);

    public static AiResult<T> Failed(AiResultStatus status, string reason) => new(status, null, null, reason);
}

/// <summary>Output value with the model's self-reported confidence (0..1). Always advisory.</summary>
public sealed record AdvisoryField<T>(T Value, decimal Confidence);
