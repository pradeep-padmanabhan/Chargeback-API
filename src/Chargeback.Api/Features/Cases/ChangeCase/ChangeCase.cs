using System.Globalization;
using System.Text.Json.Serialization;
using Chargeback.Api.Common.Idempotency;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Cases.Contracts;
using Chargeback.Api.Features.Cases.CreateCase;
using Chargeback.Api.Features.Cases.GetCases;
using Chargeback.Infrastructure.Persistence;
using Chargeback.Infrastructure.Persistence.Entities;
using Chargeback.Infrastructure.Persistence.Queries;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Events;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Chargeback.Api.Features.Cases.ChangeCase;

/// <summary>
/// Optimistic concurrency: the case version (PostgreSQL xmin). <c>/transitions</c> takes it as <c>expectedVersion</c>
/// in the body; <c>/assignment</c> takes it as ETag / If-Match.
/// </summary>
public static class CaseVersion
{
    public static string ToETag(uint version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <returns>null when the precondition holds; otherwise 428 (missing) or 412 (malformed or stale).</returns>
    public static Error? Check(string? ifMatch, uint current)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return CaseErrors.VersionRequired;
        }

        var value = ifMatch.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        value = value.Trim('"');
        return uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var expected) && expected == current
            ? null
            : CaseErrors.VersionMismatch;
    }
}

public sealed record CaseStatusChanged : DomainEvent
{
    public override string EventType => "case.status.changed";

    public required string FromStatus { get; init; }

    public required string ToStatus { get; init; }

    public required string Action { get; init; }

    /// <summary>The analyst's rationale; null when the action does not require one and none was given.</summary>
    public required string? Reason { get; init; }

    public required Guid ChangedBy { get; init; }
}

public sealed record CaseAssigned : DomainEvent
{
    public override string EventType => "case.assigned";

    public required Guid? FromUserId { get; init; }

    public required Guid? ToUserId { get; init; }

    public required Guid AssignedBy { get; init; }
}

/// <summary>
/// Lifecycle action (common guide v1.4 §3.2): START_REVIEW, FLAG, UNFLAG, CLOSE. FILED → CLOSED is admin-only; the
/// handler checks that from the current status. Idempotent (ADR-0106, atomic mode).
/// </summary>
[RequirePermission(Permissions.UpdateCaseStatus)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
[Idempotent("transitionCase", IdempotencyMode.Atomic, StatusCodes.Status200OK)]
public sealed record TransitionCaseCommand(Guid CaseId, TransitionCaseRequest Body, [property: JsonIgnore] string? IdempotencyKey = null)
    : ICommand<CaseDetailDto>, IResourceScopedRequest, ITransactionalCommand, IIdempotentCommand
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

public sealed class TransitionCaseValidator : AbstractValidator<TransitionCaseCommand>
{
    public const int MaxRationaleLength = 1000;

    public TransitionCaseValidator()
    {
        RuleFor(x => x.Body).NotNull();
        When(x => x.Body is not null, () =>
        {
            RuleFor(x => x.Body.Action)
                .Must(CaseActions.IsKnown)
                .WithMessage($"Action must be one of: {string.Join(", ", CaseActions.All)}.")
                .OverridePropertyName("action");
            RuleFor(x => x.Body.ExpectedVersion)
                .NotNull().WithMessage("expectedVersion is required: send the case version from GET /cases/{caseId}.")
                .OverridePropertyName("expectedVersion");
            RuleFor(x => x.Body.Rationale)
                .NotEmpty().WithMessage("A rationale is required for this action.")
                .When(x => CaseActions.RationaleRequired.Contains(x.Body.Action, StringComparer.Ordinal))
                .OverridePropertyName("rationale");
            RuleFor(x => x.Body.Rationale)
                .MaximumLength(MaxRationaleLength)
                .Must(r => !PanRedactor.ContainsPan(r)).WithMessage("Must not contain a card number.")
                .When(x => x.Body.Rationale is not null)
                .OverridePropertyName("rationale");
        });
    }
}

[RequirePermission(Permissions.AssignCase)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record UpdateCaseAssignmentCommand(Guid CaseId, UpdateCaseAssignmentRequest Body, string? IfMatch)
    : ICommand<CaseDetailDto>, IResourceScopedRequest, ITransactionalCommand
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

internal sealed class TransitionCaseHandler(ChargebackDbContext db, ICaseDetailReader reader, ICurrentUser currentUser, IdempotencyContext idempotency)
    : IRequestHandler<TransitionCaseCommand, Result<CaseDetailDto>>
{
    public async Task<Result<CaseDetailDto>> Handle(TransitionCaseCommand request, CancellationToken cancellationToken)
    {
        var @case = await db.Cases.SingleOrDefaultAsync(c => c.Id == request.CaseId, cancellationToken);
        if (@case is null)
        {
            return Errors.ResourceNotFound;
        }

        if (request.Body.ExpectedVersion != @case.Version)
        {
            return CaseErrors.StaleVersion;
        }

        var from = @case.Status;
        var action = request.Body.Action;
        var transition = CaseActions.IsTransition(action) ? CaseStatusTransitions.Find(from, action) : null;
        if (transition is not { Actor: TransitionActor.Analyst or TransitionActor.Admin })
        {
            return CaseErrors.InvalidTransition(action, from);
        }

        if (!CaseStatusTransitions.IsPermitted(transition.Actor, currentUser))
        {
            return Errors.UserTypeNotAllowed;
        }

        var bankId = await db.Disputes.Where(d => d.Id == @case.DisputeId).Select(d => d.BankId).SingleAsync(cancellationToken);

        // Timeline first (ADR-0109): the event row is written before the status changes, in the same transaction.
        @case.AddDomainEvent(new CaseStatusChanged
        {
            CaseId = @case.Id,
            BankId = bankId,
            FromStatus = from,
            ToStatus = transition.To,
            Action = action,
            Reason = string.IsNullOrWhiteSpace(request.Body.Rationale) ? null : request.Body.Rationale.Trim(),
            ChangedBy = currentUser.UserId,
        });
        await db.SaveChangesAsync(cancellationToken);

        @case.Status = transition.To;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return CaseErrors.StaleVersion;
        }

        var dto = (await reader.ReadAsync(@case.Id, cancellationToken))!;

        // Committed by TransactionBehavior together with the status change (ADR-0106 atomic mode).
        idempotency.RecordCompleted(db, dto, @case.Id);
        return dto;
    }
}

internal sealed class UpdateCaseAssignmentHandler(ChargebackDbContext db, IDapperQueryService queries, ICaseDetailReader reader, ICurrentUser currentUser, TimeProvider timeProvider)
    : IRequestHandler<UpdateCaseAssignmentCommand, Result<CaseDetailDto>>
{
    // An assignee must be able to work the case: active processor/admin, active role with VIEW_CASES, and a
    // currently valid scope for the case's bank (design choice; see ADR-0110 notes).
    private const string EligibilitySql = """
        SELECT EXISTS (
          SELECT 1
          FROM chargeback_diagram.users u
          JOIN chargeback_diagram.roles r ON r.id = u.role_id AND r.is_active
          JOIN chargeback_diagram.role_permissions rp ON rp.role_id = r.id
          JOIN chargeback_diagram.permissions p ON p.id = rp.permission_id AND p.is_active AND p.name = 'VIEW_CASES'
          JOIN chargeback_diagram.user_bank_scopes s ON s.user_id = u.id AND s.bank_id = @BankId
               AND s.valid_from <= @Now AND (s.valid_until IS NULL OR s.valid_until > @Now)
          WHERE u.id = @UserId AND u.status = 'ACTIVE' AND u.user_type IN ('PROCESSOR', 'ADMIN') AND r.role_type = u.user_type)
        """;

    public async Task<Result<CaseDetailDto>> Handle(UpdateCaseAssignmentCommand request, CancellationToken cancellationToken)
    {
        var @case = await db.Cases.SingleOrDefaultAsync(c => c.Id == request.CaseId, cancellationToken);
        if (@case is null)
        {
            return Errors.ResourceNotFound;
        }

        if (CaseVersion.Check(request.IfMatch, @case.Version) is { } precondition)
        {
            return precondition;
        }

        var target = request.Body?.AssignedTo;
        var bankId = await db.Disputes.Where(d => d.Id == @case.DisputeId).Select(d => d.BankId).SingleAsync(cancellationToken);
        if (target is { } userId
            && !await queries.ExecuteScalarAsync<bool>(EligibilitySql, new { UserId = userId, BankId = bankId, Now = timeProvider.GetUtcNow() }, cancellationToken))
        {
            return CaseErrors.AssigneeNotEligible;
        }

        if (target == @case.AssignedTo)
        {
            return (await reader.ReadAsync(@case.Id, cancellationToken))!;
        }

        @case.AddDomainEvent(new CaseAssigned
        {
            CaseId = @case.Id,
            BankId = bankId,
            FromUserId = @case.AssignedTo,
            ToUserId = target,
            AssignedBy = currentUser.UserId,
        });
        await db.SaveChangesAsync(cancellationToken);

        @case.AssignedTo = target;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return CaseErrors.VersionMismatch;
        }

        return (await reader.ReadAsync(@case.Id, cancellationToken))!;
    }
}
