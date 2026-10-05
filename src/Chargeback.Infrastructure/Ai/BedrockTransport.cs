namespace Chargeback.Infrastructure.Ai;

/// <summary>Request sent to the model. Temperature is fixed at 0 by <see cref="BedrockAiClient"/>.</summary>
public sealed record BedrockTransportRequest(
    string ModelId,
    string PromptTemplateId,
    string PromptTemplateVersion,
    string MaskedInput,
    decimal Temperature,
    int MaxOutputTokens);

public sealed record BedrockTransportResponse(string Content, string ModelId, int? InputTokens, int? OutputTokens);

/// <summary>
/// The raw AWS Bedrock runtime call. Isolated so the client's masking, timeout, retry,
/// validation and audit logic is testable without AWS. The AWS implementation is added once
/// the model id / region / prompt ownership are approved (open question Q11).
/// </summary>
public interface IBedrockTransport
{
    Task<BedrockTransportResponse> InvokeAsync(BedrockTransportRequest request, CancellationToken cancellationToken);
}

/// <summary>Retryable failure (throttling, 5xx).</summary>
public sealed class AiTransientException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Non-retryable: the transport is not configured or the model is unavailable.</summary>
public sealed class AiUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed class NotConfiguredBedrockTransport : IBedrockTransport
{
    public Task<BedrockTransportResponse> InvokeAsync(BedrockTransportRequest request, CancellationToken cancellationToken) =>
        throw new AiUnavailableException("Bedrock transport is not configured in this environment.");
}
