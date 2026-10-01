using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Cases.Contracts;
using Chargeback.Api.Features.Cases.CreateCase;
using Chargeback.Api.Features.Triage.EvaluateCaseTriage;
using Chargeback.Infrastructure.Outbox;
using Chargeback.SharedKernel.Results;
using MediatR;

namespace Chargeback.Api.Workers;

/// <summary>A consumer could not complete and wants the event delivered again (at-least-once).</summary>
public sealed class RetryableConsumerException(string message) : Exception(message);

/// <summary>
/// Creates a case for every dispute once its gates are evaluated (Phase 7 decision): NEW → NEW, FLAGGED → FLAGGED.
/// Idempotent via <c>processed_domain_events</c>; repeats are skipped silently and logged.
/// </summary>
public sealed partial class CaseCreationConsumer(ISender sender, ILogger<CaseCreationConsumer> logger) : IIntegrationEventConsumer
{
    public const string ConsumerName = "case-creation";
    public const string SourceEventType = "dispute.gates.evaluated";

    public string Name => ConsumerName;

    public bool Handles(string eventType) => eventType == SourceEventType;

    public async Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var disputeId = envelope.Data.GetProperty("disputeId").GetGuid();

        Result<CaseCreatedResponse> result;
        using (SystemExecution.Begin(ConsumerName))
        {
            result = await sender.Send(new CreateCaseForDisputeCommand(envelope.EventId, disputeId, ConsumerName), cancellationToken);
        }

        if (result.IsSuccess)
        {
            LogCreated(logger, result.Value.CaseReference, result.Value.Status, disputeId);
        }
        else if (result.Error == CaseErrors.AlreadyProcessed)
        {
            LogSkipped(logger, envelope.EventId, ConsumerName);
        }
        else
        {
            throw new RetryableConsumerException($"Case creation for dispute {disputeId} failed: {result.Error.Code}");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created case {CaseReference} ({Status}) for dispute {DisputeId}")]
    private static partial void LogCreated(ILogger logger, string caseReference, string status, Guid disputeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Event {EventId} already processed by {Consumer}; skipped")]
    private static partial void LogSkipped(ILogger logger, Guid eventId, string consumer);
}

/// <summary>
/// Runs automatic triage for newly created NEW cases only. FLAGGED cases are ignored: they never proceed to
/// triage automatically (analysts may re-triage manually, ADR-0124). Unavailable dependencies are retried.
/// </summary>
public sealed partial class AutomaticTriageConsumer(ISender sender, ILogger<AutomaticTriageConsumer> logger) : IIntegrationEventConsumer
{
    public const string ConsumerName = "automatic-triage";
    public const string SourceEventType = "case.created";

    public string Name => ConsumerName;

    public bool Handles(string eventType) => eventType == SourceEventType;

    public async Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Data.GetProperty("status").GetString() != CaseStatuses.New || envelope.CaseId is not { } caseId)
        {
            return;
        }

        Result<Features.Triage.Contracts.TriageResultDto> result;
        using (SystemExecution.Begin(ConsumerName))
        {
            result = await sender.Send(new EvaluateCaseTriageCommand(caseId, envelope.EventId, ConsumerName), cancellationToken);
        }

        if (result.IsSuccess)
        {
            return;
        }

        if (result.Error == TriageErrors.AlreadyProcessed)
        {
            LogSkipped(logger, envelope.EventId, ConsumerName);
        }
        else if (result.Error == TriageErrors.Unavailable || result.Error == TriageErrors.ConcurrentChange)
        {
            throw new RetryableConsumerException($"Automatic triage for case {caseId} must be retried: {result.Error.Code}");
        }
        else
        {
            // Not eligible any more (e.g. an analyst already moved the case): nothing to do.
            LogNotTriaged(logger, caseId, result.Error.Code);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Event {EventId} already processed by {Consumer}; skipped")]
    private static partial void LogSkipped(ILogger logger, Guid eventId, string consumer);

    [LoggerMessage(Level = LogLevel.Information, Message = "Case {CaseId} not triaged automatically: {Reason}")]
    private static partial void LogNotTriaged(ILogger logger, Guid caseId, string reason);
}

public static class WorkerServiceRegistration
{
    public static IServiceCollection AddWorkflowConsumers(this IServiceCollection services)
    {
        services.AddScoped<IIntegrationEventConsumer, CaseCreationConsumer>();
        services.AddScoped<IIntegrationEventConsumer, AutomaticTriageConsumer>();
        return services;
    }
}
