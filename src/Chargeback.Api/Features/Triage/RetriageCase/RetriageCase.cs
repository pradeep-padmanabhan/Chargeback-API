using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Cases.Contracts;
using Chargeback.Api.Features.Triage.Contracts;
using Chargeback.Api.Features.Triage.EvaluateCaseTriage;
using Chargeback.Infrastructure.Persistence;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Chargeback.Api.Features.Triage.RetriageCase;

public sealed record RetriageRequest(string? Reason);

/// <summary>
/// ADR-0124 (approved): manual, analyst-only re-triage of a FLAGGED or UNDER_REVIEW case, by explicit action
/// with a recorded reason. Never automatic. Does not change the case status; the outcome remains a recommendation.
/// Requires RETRIAGE_CASE (common guide v1.5 §3.1; supersedes ADR-0124's UPDATE_CASE_STATUS).
/// </summary>
[RequirePermission(Permissions.RetriageCase)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record RetriageCaseCommand(Guid CaseId, RetriageRequest Body) : ICommand<TriageResultDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

public sealed class RetriageCaseValidator : AbstractValidator<RetriageCaseCommand>
{
    public const int MaxReasonLength = 1000;

    public RetriageCaseValidator()
    {
        RuleFor(x => x.Body).NotNull();
        RuleFor(x => x.Body.Reason)
            .NotEmpty().WithMessage("A reason for re-triage is required.")
            .MaximumLength(MaxReasonLength)
            .Must(r => !PanRedactor.ContainsPan(r)).WithMessage("Must not contain a card number.")
            .OverridePropertyName("reason")
            .When(x => x.Body is not null);
    }
}

internal sealed class RetriageCaseHandler(ChargebackDbContext db, CaseTriageRunner runner, ICurrentUser currentUser)
    : IRequestHandler<RetriageCaseCommand, Result<TriageResultDto>>
{
    private static readonly string[] EligibleStatuses = [CaseStatuses.Flagged, CaseStatuses.UnderReview];

    public async Task<Result<TriageResultDto>> Handle(RetriageCaseCommand request, CancellationToken cancellationToken)
    {
        var @case = await db.Cases.SingleOrDefaultAsync(c => c.Id == request.CaseId, cancellationToken);
        if (@case is null)
        {
            return Errors.ResourceNotFound;
        }

        if (!EligibleStatuses.Contains(@case.Status))
        {
            return TriageErrors.RetriageNotAllowed;
        }

        var dispute = await db.Disputes.SingleAsync(d => d.Id == @case.DisputeId, cancellationToken);
        var reason = request.Body.Reason!.Trim();

        // The request is recorded before the result, in the same SaveChanges (one transaction).
        var requested = new CaseRetriageRequested
        {
            CaseId = @case.Id,
            BankId = dispute.BankId,
            RequestedBy = currentUser.UserId,
            Reason = reason,
            CaseStatus = @case.Status,
        };

        var trigger = new TriageTrigger(TriageTriggerType.Manual, currentUser.UserId, reason, null, null);
        return await runner.RunAsync(@case, dispute, trigger, requested, cancellationToken);
    }
}
