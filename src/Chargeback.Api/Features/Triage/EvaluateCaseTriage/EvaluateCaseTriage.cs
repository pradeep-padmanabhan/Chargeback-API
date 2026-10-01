using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Cases.Contracts;
using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Api.Features.Triage.Contracts;
using Chargeback.Infrastructure.Persistence;
using Chargeback.SharedKernel.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Chargeback.Api.Features.Triage.EvaluateCaseTriage;

/// <summary>
/// Automatic two-layer deterministic triage for one case — a workflow step, not a user action. Runs only inside
/// <see cref="SystemExecution"/> (ADR-0123) and is not mapped to any HTTP endpoint. Only NEW cases whose dispute
/// is NEW are triaged (a FLAGGED case never progresses automatically). When <paramref name="SourceEventId"/> is
/// given, the event is recorded in <c>processed_domain_events</c> atomically with the result, and a repeat is skipped.
/// </summary>
[SystemOperation("Automatic triage of NEW cases is a workflow step (common guide §6 Act 3), never a user action.")]
[NotBankScoped("Workflow step acting on one case chosen by the workflow; results stay attached to that case's bank.")]
public sealed record EvaluateCaseTriageCommand(Guid CaseId, Guid? SourceEventId = null, string? Consumer = null) : ICommand<TriageResultDto>;

internal sealed class EvaluateCaseTriageHandler(ChargebackDbContext db, CaseTriageRunner runner)
    : IRequestHandler<EvaluateCaseTriageCommand, Result<TriageResultDto>>
{
    public async Task<Result<TriageResultDto>> Handle(EvaluateCaseTriageCommand request, CancellationToken cancellationToken)
    {
        if (await runner.AlreadyProcessedAsync(request.SourceEventId, cancellationToken))
        {
            return TriageErrors.AlreadyProcessed;
        }

        var @case = await db.Cases.SingleOrDefaultAsync(c => c.Id == request.CaseId, cancellationToken);
        if (@case is null)
        {
            return Errors.ResourceNotFound;
        }

        var dispute = await db.Disputes.SingleAsync(d => d.Id == @case.DisputeId, cancellationToken);

        // A FLAGGED (or any non-NEW) dispute never progresses automatically.
        if (dispute.Status != DisputeStatuses.New)
        {
            return TriageErrors.DisputeNotEligible;
        }

        if (@case.Status != CaseStatuses.New)
        {
            return TriageErrors.CaseNotEligible;
        }

        var trigger = new TriageTrigger(TriageTriggerType.Automatic, null, null, request.SourceEventId, request.Consumer);
        return await runner.RunAsync(@case, dispute, trigger, null, cancellationToken);
    }
}
