using System.Text.Json.Serialization;
using Chargeback.Api.Common.Idempotency;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Cases.Contracts;
using Chargeback.Api.Features.Review.Contracts;
using Chargeback.Infrastructure.Persistence;
using Chargeback.Infrastructure.Persistence.Entities;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Events;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Chargeback.Api.Features.Review.Decision;

public static class ReviewErrors
{
    public static readonly Error StaleVersion = Error.Conflict(
        "CASE_VERSION_MISMATCH", "The case has changed since it was read. Reload it and retry with the new version.");

    public static readonly Error ReasonCodeNotDerived = Error.Unprocessable(
        "REASON_CODE_NOT_DERIVED",
        "The case has no deterministic reason code, so it cannot be approved. Re-triage it or reject it.");

    public static readonly Error ReasonCodeMismatch = Error.Unprocessable(
        "REASON_CODE_MISMATCH",
        "reasonCodeId must be the case's derived reason code. Reason codes come only from deterministic rules; they cannot be substituted.");

    public static readonly Error AlreadyProcessed = Error.Conflict(
        "EVENT_ALREADY_PROCESSED", "This workflow event has already been processed.");

    public static Error NotUnderReview(string status) => Error.Unprocessable(
        "INVALID_TRANSITION", $"A review decision needs a case in status UNDER_REVIEW; this case is {status}.");
}

/// <summary>The audited Human Review decision; the first entry of the decision on the case timeline.</summary>
public sealed record CaseReviewDecided : DomainEvent
{
    public override string EventType => "case.review.decided";

    public required Guid DecisionId { get; init; }

    /// <summary>APPROVED | REJECTED</summary>
    public required string Decision { get; init; }

    public required Guid? ReasonCodeId { get; init; }

    public required string Rationale { get; init; }

    public required Guid ReviewedBy { get; init; }
}

/// <summary>
/// Approve or reject a case under review (common guide §6 Act 5; ADR-0101). Approve confirms the case's deterministic
/// reason code. Writes the append-only decision row and the timeline events, then moves the case to APPROVED or
/// REJECTED, in one transaction. Idempotent (ADR-0106, atomic mode). A rejection's client notification follows
/// from <c>case.review.decided</c> (Phase 11).
/// </summary>
[RequirePermission(Permissions.ReviewCase)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
[Idempotent("submitReviewDecision", IdempotencyMode.Atomic, StatusCodes.Status200OK)]
public sealed record SubmitReviewDecisionCommand(Guid CaseId, ReviewDecisionRequest Body, [property: JsonIgnore] string? IdempotencyKey = null)
    : ICommand<ReviewDecisionResponse>, IResourceScopedRequest, ITransactionalCommand, IIdempotentCommand
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

public sealed class SubmitReviewDecisionValidator : AbstractValidator<SubmitReviewDecisionCommand>
{
    public const int MaxRationaleLength = 4000;

    public SubmitReviewDecisionValidator()
    {
        RuleFor(x => x.Body).NotNull();
        When(x => x.Body is not null, () =>
        {
            RuleFor(x => x.Body.Decision)
                .NotNull().WithMessage("decision is required: Approve or Reject.")
                .IsInEnum()
                .OverridePropertyName("decision");
            RuleFor(x => x.Body.Rationale)
                .NotEmpty().WithMessage("A rationale is required for every review decision.")
                .MaximumLength(MaxRationaleLength)
                .Must(r => !PanRedactor.ContainsPan(r)).WithMessage("Must not contain a card number.")
                .OverridePropertyName("rationale");
            RuleFor(x => x.Body.ExpectedVersion)
                .NotNull().WithMessage("expectedVersion is required: send the case version from GET /cases/{caseId}.")
                .OverridePropertyName("expectedVersion");
            RuleFor(x => x.Body.ReasonCodeId)
                .NotNull().WithMessage("Approve requires reasonCodeId: confirm the case's derived reason code.")
                .When(x => x.Body.Decision == ReviewDecision.Approve)
                .OverridePropertyName("reasonCodeId");
            RuleFor(x => x.Body.ReasonCodeId)
                .Null().WithMessage("reasonCodeId applies only to Approve.")
                .When(x => x.Body.Decision == ReviewDecision.Reject)
                .OverridePropertyName("reasonCodeId");
        });
    }
}

internal sealed class SubmitReviewDecisionHandler(
    ChargebackDbContext db, ICaseDetailReader reader, ICurrentUser currentUser, IdempotencyContext idempotency, TimeProvider timeProvider)
    : IRequestHandler<SubmitReviewDecisionCommand, Result<ReviewDecisionResponse>>
{
    public async Task<Result<ReviewDecisionResponse>> Handle(SubmitReviewDecisionCommand request, CancellationToken cancellationToken)
    {
        var @case = await db.Cases.SingleOrDefaultAsync(c => c.Id == request.CaseId, cancellationToken);
        if (@case is null)
        {
            return Errors.ResourceNotFound;
        }

        if (request.Body.ExpectedVersion != @case.Version)
        {
            return ReviewErrors.StaleVersion;
        }

        var decision = request.Body.Decision!.Value;
        var action = decision == ReviewDecision.Approve ? CaseActions.Approve : CaseActions.Reject;
        var transition = CaseStatusTransitions.Find(@case.Status, action);
        if (transition is not { Actor: TransitionActor.ReviewDecision })
        {
            return ReviewErrors.NotUnderReview(@case.Status);
        }

        if (decision == ReviewDecision.Approve)
        {
            if (@case.DerivedReasonCodeId is null)
            {
                return ReviewErrors.ReasonCodeNotDerived;
            }

            if (request.Body.ReasonCodeId != @case.DerivedReasonCodeId)
            {
                return ReviewErrors.ReasonCodeMismatch;
            }
        }

        var now = timeProvider.GetUtcNow();
        var rationale = request.Body.Rationale!.Trim();
        var bankId = await db.Disputes.Where(d => d.Id == @case.DisputeId).Select(d => d.BankId).SingleAsync(cancellationToken);
        var record = new CaseReviewDecision
        {
            CaseId = @case.Id,
            Decision = transition.To,
            ReasonCodeId = decision == ReviewDecision.Approve ? @case.DerivedReasonCodeId : null,
            Rationale = rationale,
            ReviewedBy = currentUser.UserId,
            DecidedAt = now,
        };
        db.CaseReviewDecisions.Add(record);

        // Audit row and timeline first (ADR-0109), then the status, in the same transaction.
        @case.AddDomainEvent(new CaseReviewDecided
        {
            CaseId = @case.Id,
            BankId = bankId,
            DecisionId = record.Id,
            Decision = record.Decision,
            ReasonCodeId = record.ReasonCodeId,
            Rationale = rationale,
            ReviewedBy = currentUser.UserId,
        });
        @case.AddDomainEvent(new CaseStatusChanged
        {
            CaseId = @case.Id,
            BankId = bankId,
            FromStatus = @case.Status,
            ToStatus = transition.To,
            Action = action,
            Reason = rationale,
            ChangedBy = currentUser.UserId,
        });
        await db.SaveChangesAsync(cancellationToken);

        @case.Status = transition.To;
        @case.HumanReviewVerdict = transition.To;
        @case.HumanReviewedBy = currentUser.UserId;
        @case.HumanReviewedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ReviewErrors.StaleVersion;
        }

        var response = new ReviewDecisionResponse(
            record.Id, @case.Id, decision, record.ReasonCodeId, rationale, currentUser.UserId, now,
            (await reader.ReadAsync(@case.Id, cancellationToken))!);

        // Committed by TransactionBehavior together with the decision (ADR-0106 atomic mode).
        idempotency.RecordCompleted(db, response, @case.Id);
        return response;
    }
}
