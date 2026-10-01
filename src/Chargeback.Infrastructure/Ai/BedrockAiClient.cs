using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chargeback.Infrastructure.Persistence;
using Chargeback.SharedKernel.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chargeback.Infrastructure.Ai;

/// <summary>
/// The single gateway to the model for all six capabilities.
/// Guarantees: never inside a DB transaction; feature-flagged per capability/bank; PAN masked
/// before input and in stored responses; temperature 0; bounded timeout and retries; strict typed
/// JSON output validated by the capability; every invocation audited in <c>ai_decision_logs</c>;
/// every failure is fail-soft (a status, never an exception to the workflow).
/// </summary>
public interface IBedrockAiClient
{
    Task<AiResult<T>> InvokeAsync<T>(AiInvocation invocation, Func<T, bool>? isValid, CancellationToken cancellationToken)
        where T : class;
}

internal sealed partial class BedrockAiClient(
    IBedrockTransport transport,
    IAiDecisionLogWriter auditLog,
    IOptions<AiOptions> options,
    TimeProvider timeProvider,
    ILogger<BedrockAiClient> logger) : IBedrockAiClient
{
    internal static readonly JsonSerializerOptions OutputJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<AiResult<T>> InvokeAsync<T>(AiInvocation invocation, Func<T, bool>? isValid, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ExternalCallGuard.ThrowIfInDatabaseTransaction("Bedrock");

        var settings = options.Value;
        if (!settings.IsEnabled(invocation.CapabilityName, invocation.BankId))
        {
            return AiResult<T>.Failed(AiResultStatus.Disabled, "CAPABILITY_DISABLED");
        }

        var maskedInput = PanRedactor.Redact(invocation.Input);
        var inputHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(maskedInput)));
        var request = new BedrockTransportRequest(
            settings.ModelId,
            invocation.PromptTemplateId,
            invocation.PromptTemplateVersion,
            maskedInput,
            Temperature: 0m,
            settings.MaxOutputTokens);

        var started = Stopwatch.GetTimestamp();
        var (response, failure) = await InvokeWithRetryAsync(request, settings, cancellationToken);
        var latencyMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        AiResult<T> result;
        string? maskedRaw = null;
        if (response is null)
        {
            result = AiResult<T>.Failed(AiResultStatus.Unavailable, failure!);
        }
        else
        {
            maskedRaw = PanRedactor.Redact(response.Content);
            var output = TryParse<T>(maskedRaw);
            result = output is not null && (isValid?.Invoke(output) ?? true)
                ? AiResult<T>.Success(output, response.ModelId)
                : AiResult<T>.Failed(AiResultStatus.InvalidOutput, "OUTPUT_SCHEMA_INVALID");
        }

        var entry = new AiDecisionLogEntry(
            Guid.CreateVersion7(),
            invocation.CaseId,
            invocation.AgentName,
            invocation.CapabilityName,
            response?.ModelId ?? settings.ModelId,
            $"{invocation.PromptTemplateId}@{invocation.PromptTemplateVersion}",
            inputHash,
            maskedRaw is null ? null : JsonSerializer.Serialize(new { content = maskedRaw, status = result.Status.ToString(), failure = result.FailureReason }),
            result.IsSuccess ? JsonSerializer.Serialize(result.Output, OutputJsonOptions) : null,
            latencyMs,
            response?.InputTokens,
            response?.OutputTokens,
            timeProvider.GetUtcNow());

        try
        {
            await auditLog.WriteAsync(entry, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unaudited AI output must not be used.
            LogAuditFailed(logger, invocation.CapabilityName, ex);
            return AiResult<T>.Failed(AiResultStatus.Unavailable, "AUDIT_WRITE_FAILED");
        }

        return result;
    }

    private async Task<(BedrockTransportResponse? Response, string? Failure)> InvokeWithRetryAsync(
        BedrockTransportRequest request, AiOptions settings, CancellationToken cancellationToken)
    {
        var failure = "UNKNOWN";
        for (var attempt = 1; attempt <= Math.Max(1, settings.MaxAttempts); attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.Timeout);
            try
            {
                return (await transport.InvokeAsync(request, timeout.Token), null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failure = "TIMEOUT";
            }
            catch (AiTransientException)
            {
                failure = "TRANSIENT_FAILURE";
            }
            catch (AiUnavailableException)
            {
                return (null, "NOT_AVAILABLE");
            }

            LogAttemptFailed(logger, request.PromptTemplateId, attempt, failure);
        }

        return (null, failure);
    }

    private static T? TryParse<T>(string content)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(content, OutputJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI invocation for template {PromptTemplateId} attempt {Attempt} failed: {Failure}")]
    private static partial void LogAttemptFailed(ILogger logger, string promptTemplateId, int attempt, string failure);

    [LoggerMessage(Level = LogLevel.Error, Message = "AI decision audit write failed for {Capability}; output discarded")]
    private static partial void LogAuditFailed(ILogger logger, string capability, Exception exception);
}
