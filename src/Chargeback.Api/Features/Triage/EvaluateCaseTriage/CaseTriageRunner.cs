using System.Text.Json;
using Chargeback.Api.Features.Triage.Conditions;
using Chargeback.Api.Features.Triage.Contracts;
using Chargeback.Api.Features.Triage.IssuerTriage;
using Chargeback.Api.Features.Triage.SchemeRules;
using Chargeback.Infrastructure.Persistence;
using Chargeback.Infrastructure.Persistence.Entities;
using Chargeback.SharedKernel.Events;
using Chargeback.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chargeback.Api.Features.Triage.EvaluateCaseTriage;

public enum TriageTriggerType
{
    /// <summary>Workflow step after case creation for a NEW case (system-only).</summary>
    Automatic,

    /// <summary>Explicit analyst re-triage with a recorded reason (ADR-0124).</summary>
    Manual,
}

/// <summary>Why a triage evaluation ran; recorded on the <c>triage.completed</c> audit event.</summary>
public sealed record TriageTrigger(TriageTriggerType Type, Guid? RequestedBy, string? Reason, Guid? SourceEventId, string? Consumer);

public static class TriageErrors
{
    public static readonly Error DisputeNotEligible = Error.Conflict(
        "DISPUTE_NOT_ELIGIBLE_FOR_TRIAGE", "Only disputes in status NEW (all ten gates passed) are triaged automatically.");

    public static readonly Error CaseNotEligible = Error.Conflict(
        "CASE_NOT_ELIGIBLE_FOR_AUTOMATIC_TRIAGE", "Automatic triage runs only for cases in status NEW.");

    public static readonly Error RetriageNotAllowed = Error.Conflict(
        "RETRIAGE_NOT_ALLOWED", "Re-triage is allowed only for cases in status FLAGGED or UNDER_REVIEW (ADR-0124).");

    public static readonly Error AlreadyProcessed = Error.Conflict(
        "EVENT_ALREADY_PROCESSED", "This workflow event has already been processed.");

    public static readonly Error Unavailable = Error.Unavailable(
        "TRIAGE_UNAVAILABLE", "Triage inputs could not be read; nothing was decided or recorded. Retry later.");

    public static readonly Error ConcurrentChange = Error.Conflict(
        "CASE_CHANGED_DURING_TRIAGE", "The case changed while triage was running; nothing was recorded. Retry.");
}

/// <summary>Audit record of one triage evaluation (the durable audit trail until ADR-0103 adds columns).</summary>
public sealed record TriageCompleted : DomainEvent
{
    public override string EventType => "triage.completed";

    public required Guid DisputeId { get; init; }

    public required Guid TriageResultId { get; init; }

    public required TriageEvaluationStatus Status { get; init; }

    public required string? Outcome { get; init; }

    public required string DecidingLayer { get; init; }

    public required string Detail { get; init; }

    public required TriageTrigger Trigger { get; init; }

    public required SchemeAudit Scheme { get; init; }

    public required IssuerAudit Issuer { get; init; }
}

/// <summary>Recorded before the re-triage result, in the same transaction (ADR-0124).</summary>
public sealed record CaseRetriageRequested : DomainEvent
{
    public override string EventType => "case.retriage.requested";

    public required Guid RequestedBy { get; init; }

    public required string Reason { get; init; }

    public required string CaseStatus { get; init; }
}

public sealed record SchemeAudit(
    string Status,
    string Detail,
    DateOnly? EvaluationDate,
    Guid? RuleSpecId,
    Guid? ReasonCodeId,
    string? ReasonCode,
    string DeadlineStatus,
    DateOnly? ClockStartDate,
    DateOnly? FilingDeadlineDate,
    int? TimeLimitDays,
    IReadOnlyList<RequiredDocument> RequiredDocuments,
    IReadOnlyList<RuleCandidateAudit> Candidates,
    RuleExclusions? Exclusions);

public sealed record IssuerAudit(string? ConfigurationVersion, IReadOnlyList<ComponentAudit> Components);

/// <summary>
/// Runs both triage layers for one case and persists the result, case derivation and audit event(s) in one
/// atomic SaveChanges. Callers are responsible for eligibility (automatic vs. manual rules differ).
/// Nothing is persisted when a dependency is unavailable, the case changed concurrently, or the source event
/// was already processed. Triage never changes the case status.
/// </summary>
internal sealed partial class CaseTriageRunner(
    ChargebackDbContext db,
    ISchemeRulesEngine schemeRules,
    IIssuerTriageEngine issuerTriage,
    TimeProvider timeProvider,
    ILogger<CaseTriageRunner> logger)
{
    public Task<bool> AlreadyProcessedAsync(Guid? sourceEventId, CancellationToken cancellationToken) =>
        sourceEventId is { } id
            ? db.ProcessedDomainEvents.AnyAsync(p => p.EventId == id, cancellationToken)
            : Task.FromResult(false);

    public async Task<Result<TriageResultDto>> RunAsync(
        Case @case, Dispute dispute, TriageTrigger trigger, DomainEvent? precedingEvent, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var facts = CaseFacts.FromDispute(dispute, schemeRules.Calendar);
        var scheme = await schemeRules.EvaluateAsync(new SchemeEvaluationInput(facts, dispute.TransactionDate, dispute.CreatedAt), cancellationToken);
        var issuer = await issuerTriage.EvaluateAsync(dispute.BankId, facts, scheme, now, cancellationToken);

        // Safe fallback: an unreadable dependency must not produce or persist any decision.
        if (scheme.Status == SchemeEvaluationStatus.Unavailable || issuer.IsUnavailable)
        {
            LogUnavailable(logger, @case.Id, scheme.Status.ToString(), issuer.Detail);
            return TriageErrors.Unavailable;
        }

        var result = new TriageResultRecord
        {
            CaseId = @case.Id,
            TriageLayer = issuer.DecidingLayer,
            HardEligibilityPass = issuer.HardEligibilityPass,
            RoutingPolicyOutcome = issuer.RoutingRuleId,
            RiskScore = issuer.RiskScore,
            RiskFlags = JsonSerializer.Serialize(issuer.RiskFlags),
            HumanReviewTriggered = issuer.HumanReviewTriggered,
            HumanReviewReason = issuer.HumanReviewReason,
            Outcome = issuer.Outcome,
            CreatedAt = now,
        };
        db.TriageResults.Add(result);

        // The case reflects only a currently determined, approved derivation; otherwise derived fields are cleared.
        var calculated = scheme.IsDetermined && scheme.Deadline.Status == DeadlineStatus.Calculated;
        @case.DerivedReasonCodeId = scheme.IsDetermined ? scheme.ReasonCode!.Id : null;
        @case.ClockStartDate = calculated ? scheme.Deadline.ClockStartDate : null;
        @case.FilingDeadlineDate = calculated ? scheme.Deadline.DeadlineDate : null;

        if (precedingEvent is not null)
        {
            @case.AddDomainEvent(precedingEvent);
        }

        var status = issuer.IsComplete ? TriageEvaluationStatus.Complete : TriageEvaluationStatus.Incomplete;
        @case.AddDomainEvent(new TriageCompleted
        {
            CaseId = @case.Id,
            BankId = dispute.BankId,
            DisputeId = dispute.Id,
            TriageResultId = result.Id,
            Status = status,
            Outcome = issuer.Outcome?.ToString(),
            DecidingLayer = issuer.DecidingLayer,
            Detail = issuer.Detail,
            Trigger = trigger,
            Scheme = new SchemeAudit(
                scheme.Status.ToString(),
                scheme.Detail,
                scheme.EvaluationDate,
                scheme.RuleSpecId,
                scheme.ReasonCode?.Id,
                scheme.ReasonCode?.Code,
                scheme.Deadline.Status.ToString(),
                scheme.Deadline.ClockStartDate,
                scheme.Deadline.DeadlineDate,
                scheme.Deadline.TimeLimitDays,
                scheme.RequiredDocuments,
                scheme.Candidates,
                scheme.Exclusions),
            Issuer = new IssuerAudit(issuer.ConfigurationVersion, issuer.Components),
        });

        if (trigger.SourceEventId is { } sourceEventId)
        {
            db.ProcessedDomainEvents.Add(new ProcessedDomainEvent
            {
                EventId = sourceEventId,
                Consumer = trigger.Consumer ?? "unknown",
                ProcessedAt = now,
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return TriageErrors.ConcurrentChange;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent consumer recorded the same event first.
            return TriageErrors.AlreadyProcessed;
        }

        LogTriaged(logger, @case.Id, trigger.Type.ToString(), status.ToString(), issuer.Outcome?.ToString() ?? "none", scheme.Status.ToString());

        return new TriageResultDto(
            result.Id,
            result.TriageLayer,
            result.HardEligibilityPass,
            result.RoutingPolicyOutcome,
            result.RiskScore,
            issuer.RiskFlags,
            result.HumanReviewTriggered,
            result.HumanReviewReason,
            result.Outcome,
            result.CreatedAt,
            status);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Case {CaseId} triaged ({Trigger}): {Status}, outcome {Outcome}, scheme {SchemeStatus}")]
    private static partial void LogTriaged(ILogger logger, Guid caseId, string trigger, string status, string outcome, string schemeStatus);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Triage for case {CaseId} not recorded: dependency unavailable (scheme {SchemeStatus}; issuer {IssuerDetail})")]
    private static partial void LogUnavailable(ILogger logger, Guid caseId, string schemeStatus, string issuerDetail);
}
