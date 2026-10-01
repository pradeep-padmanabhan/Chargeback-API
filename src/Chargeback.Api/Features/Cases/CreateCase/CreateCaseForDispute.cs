using System.Globalization;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Cases.Contracts;
using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Infrastructure.Persistence;
using Chargeback.Infrastructure.Persistence.Entities;
using Chargeback.Infrastructure.Persistence.Queries;
using Chargeback.SharedKernel.Events;
using Chargeback.SharedKernel.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chargeback.Api.Features.Cases.CreateCase;

public sealed record CaseCreatedResponse(Guid CaseId, string CaseReference, string Status);

public static class CaseErrors
{
    public static readonly Error AlreadyProcessed = Error.Conflict(
        "EVENT_ALREADY_PROCESSED", "This workflow event has already been processed.");

    public static readonly Error UnsupportedDisputeStatus = Error.Conflict(
        "DISPUTE_STATUS_UNSUPPORTED", "Cases are created only for disputes in status NEW or FLAGGED.");

    public static readonly Error ReferenceCapacityExhausted = Error.Failure(
        "CASE_REFERENCE_CAPACITY_EXHAUSTED", "The six-digit case reference sequence is exhausted (ADR-0125).");

    public static readonly Error VersionRequired = Error.PreconditionRequired(
        "CASE_VERSION_REQUIRED", "Send the case version from GET /cases/{caseId} (ETag) in the If-Match header.");

    public static readonly Error VersionMismatch = Error.PreconditionFailed(
        "CASE_VERSION_MISMATCH", "The case has changed since it was read. Reload it and retry.");

    /// <summary>Optimistic concurrency failure on <c>/transitions</c> (body <c>expectedVersion</c>): 409, not 412.</summary>
    public static readonly Error StaleVersion = Error.Conflict(
        "CASE_VERSION_MISMATCH", "The case has changed since it was read. Reload it and retry with the new version.");

    public static Error InvalidTransition(string action, string status) => Error.Unprocessable(
        "INVALID_TRANSITION",
        CaseActions.IsTransition(action)
            ? $"{action} is not available for a case in status {status}."
            : $"{action} is not performed through /transitions: use {(action == CaseActions.File ? "/filings/{filingId}/confirmation" : "/cases/{caseId}/review/decision")}.");

    public static readonly Error AssigneeNotEligible = Error.Conflict(
        "ASSIGNEE_NOT_ELIGIBLE", "The assignee must be an active processor or admin user who can view this bank's cases.");
}

/// <summary>Case reference <c>CB-{YYYY}-{6-digit number}</c>; the number comes from a database sequence (migration 0003).</summary>
public static class CaseReference
{
    public const long MaxNumber = 999_999;

    public static string Format(int year, long number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, MaxNumber);
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 2000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, 9999);
        return string.Create(CultureInfo.InvariantCulture, $"CB-{year:D4}-{number:D6}");
    }
}

public interface ICaseReferenceGenerator
{
    /// <returns>The next reference, or a failure when the six-digit sequence is exhausted.</returns>
    Task<Result<string>> NextAsync(CancellationToken cancellationToken);
}

internal sealed class SequenceCaseReferenceGenerator(IDapperQueryService db, TimeProvider timeProvider) : ICaseReferenceGenerator
{
    /// <summary>SQLSTATE 2200H: sequence_generator_limit_exceeded.</summary>
    private const string SequenceGeneratorLimitExceeded = "2200H";

    public async Task<Result<string>> NextAsync(CancellationToken cancellationToken)
    {
        try
        {
            var number = await db.ExecuteScalarAsync<long>("SELECT nextval('chargeback_diagram.case_reference_seq')", null, cancellationToken);

            // Year of creation in UTC: the calendar time zone is not approved (ADR-0122); see ADR-0125.
            return CaseReference.Format(timeProvider.GetUtcNow().Year, number);
        }
        catch (PostgresException ex) when (ex.SqlState == SequenceGeneratorLimitExceeded)
        {
            return CaseErrors.ReferenceCapacityExhausted;
        }
    }
}

/// <summary>Raised with the new case, in the same transaction; the first entry of the case timeline.</summary>
public sealed record CaseCreated : DomainEvent
{
    public override string EventType => "case.created";

    public required Guid DisputeId { get; init; }

    public required string CaseReference { get; init; }

    public required string Status { get; init; }

    public required Guid SourceEventId { get; init; }
}

/// <summary>
/// Creates the case for a dispute after its gates were evaluated (every dispute gets a case): NEW → NEW,
/// FLAGGED → FLAGGED (analyst queue; never triaged automatically). A workflow step, idempotent per source
/// event via <c>processed_domain_events</c> and per dispute via <c>cases.dispute_id UNIQUE</c>.
/// </summary>
[SystemOperation("Case creation after intake is a workflow step (common guide §6 Acts 2-4), never a user action.")]
[NotBankScoped("Workflow step for one dispute chosen by the workflow; the case belongs to that dispute's bank.")]
public sealed record CreateCaseForDisputeCommand(Guid SourceEventId, Guid DisputeId, string Consumer) : ICommand<CaseCreatedResponse>;

internal sealed class CreateCaseForDisputeHandler(ChargebackDbContext db, ICaseReferenceGenerator references, TimeProvider timeProvider)
    : IRequestHandler<CreateCaseForDisputeCommand, Result<CaseCreatedResponse>>
{
    public async Task<Result<CaseCreatedResponse>> Handle(CreateCaseForDisputeCommand request, CancellationToken cancellationToken)
    {
        if (await db.ProcessedDomainEvents.AnyAsync(p => p.EventId == request.SourceEventId, cancellationToken))
        {
            return CaseErrors.AlreadyProcessed;
        }

        var dispute = await db.Disputes.SingleOrDefaultAsync(d => d.Id == request.DisputeId, cancellationToken);
        if (dispute is null)
        {
            return Errors.ResourceNotFound;
        }

        var status = dispute.Status switch
        {
            DisputeStatuses.New => CaseStatuses.New,
            DisputeStatuses.Flagged => CaseStatuses.Flagged,
            _ => null,
        };
        if (status is null)
        {
            return CaseErrors.UnsupportedDisputeStatus;
        }

        var processed = new ProcessedDomainEvent { EventId = request.SourceEventId, Consumer = request.Consumer, ProcessedAt = timeProvider.GetUtcNow() };

        // Defensive: a case already exists for the dispute (e.g. a different event re-announced it).
        if (await db.Cases.AnyAsync(c => c.DisputeId == dispute.Id, cancellationToken))
        {
            db.ProcessedDomainEvents.Add(processed);
            await SaveIgnoringDuplicates(cancellationToken);
            return CaseErrors.AlreadyProcessed;
        }

        var reference = await references.NextAsync(cancellationToken);
        if (reference.IsFailure)
        {
            return reference.Error;
        }

        var @case = new Case { DisputeId = dispute.Id, CaseReference = reference.Value, Status = status };
        @case.AddDomainEvent(new CaseCreated
        {
            CaseId = @case.Id,
            BankId = dispute.BankId,
            DisputeId = dispute.Id,
            CaseReference = reference.Value,
            Status = status,
            SourceEventId = request.SourceEventId,
        });
        db.Cases.Add(@case);
        db.ProcessedDomainEvents.Add(processed);

        // Case, its case.created event and the processed-event record commit together.
        return await SaveIgnoringDuplicates(cancellationToken)
            ? new CaseCreatedResponse(@case.Id, @case.CaseReference, @case.Status)
            : CaseErrors.AlreadyProcessed;
    }

    /// <returns>false when a concurrent consumer already recorded the event or created the case.</returns>
    private async Task<bool> SaveIgnoringDuplicates(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }
}
