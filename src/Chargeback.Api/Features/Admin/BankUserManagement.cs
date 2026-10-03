using System.Security.Cryptography;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Admin.Contracts;
using Chargeback.Infrastructure.Persistence;
using Chargeback.Infrastructure.Persistence.Entities;
using Chargeback.Infrastructure.Persistence.Queries;
using Chargeback.SharedKernel.Events;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Chargeback.Api.Features.Admin;

// Bank user management (Admin & Configuration). Processor and admin users with MANAGE_BANK_USERS and scope for the bank
// (a bank-admin role is deferred). Every change is audited as a domain event (domain_events, append-only by convention).

public static class UserStatuses
{
    public const string Active = "ACTIVE";
    public const string Disabled = "DISABLED";

    /// <summary>Placeholder <c>cognito_sub</c> for an invited user until Cognito identity linking exists.</summary>
    public static string PendingInviteSub(Guid userId) => $"pending-invite:{userId:N}";

    public static bool IsPendingInvite(string cognitoSub) => cognitoSub.StartsWith("pending-invite:", StringComparison.Ordinal);
}

public static class BankUserErrors
{
    public static readonly Error RoleNotAssignable = Error.Unprocessable(
        "ROLE_NOT_ASSIGNABLE", "roleId must be an active BANK-type role (e.g. \"Bank User\").");

    public static readonly Error EmailInUse = Error.Conflict(
        "USER_EMAIL_IN_USE", "A user with this email already exists.");

    public static readonly Error InvitePending = Error.Conflict(
        "INVITE_PENDING", "An invited user stays DISABLED until their sign-in identity is linked; it cannot be activated yet.");

    public static readonly Error CannotDeleteSelf = Error.Conflict(
        "CANNOT_DELETE_SELF", "You cannot remove your own user.");

    public static readonly Error ConcurrentChange = Error.Conflict(
        "USER_CHANGED", "The user changed while this request was processed. Reload it and retry.");
}

/// <summary>Guards shared by the user-management handlers.</summary>
public static class BankUserRules
{
    public const string InviteDelivery = "NOT_SENT: KNOWN_LIMITATION_INVITE_EMAIL_ (no email is sent; the token is a placeholder)";

    /// <summary>A bank user may hold only an active BANK-type role (role type must match user type).</summary>
    public static bool IsAssignable(Role? role) => role is { IsActive: true, RoleType: UserType.Bank };

    /// <summary>Activation is refused while the identity is a pending-invite placeholder.</summary>
    public static bool CanActivate(string cognitoSub) => !UserStatuses.IsPendingInvite(cognitoSub);

    public static bool CanDelete(Guid targetUserId, Guid callerUserId) => targetUserId != callerUserId;

    /// <summary>KNOWN_LIMITATION_INVITE_EMAIL_: placeholder only; never stored, never redeemable.</summary>
    public static string NewInviteToken() => "KNOWN_LIMITATION_INVITE_EMAIL_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}

public sealed record UserInvited : DomainEvent
{
    public override string EventType => "user.invited";

    public required Guid UserId { get; init; }

    public required Guid RoleId { get; init; }

    public required Guid InvitedBy { get; init; }
}

/// <summary>Name or active-state change. Role changes are recorded separately as <see cref="UserRoleChanged"/>.</summary>
public sealed record UserUpdated : DomainEvent
{
    public override string EventType => "user.updated";

    public required Guid UserId { get; init; }

    public required IReadOnlyList<string> ChangedFields { get; init; }

    public required string FromStatus { get; init; }

    public required string ToStatus { get; init; }

    public required Guid UpdatedBy { get; init; }
}

/// <summary>Immutable audit of every role change.</summary>
public sealed record UserRoleChanged : DomainEvent
{
    public override string EventType => "user.role.changed";

    public required Guid UserId { get; init; }

    public required Guid FromRoleId { get; init; }

    public required Guid ToRoleId { get; init; }

    public required Guid ChangedBy { get; init; }
}

public sealed record UserDeleted : DomainEvent
{
    public override string EventType => "user.deleted";

    public required Guid UserId { get; init; }

    public required Guid DeletedBy { get; init; }
}

[RequirePermission(Permissions.ManageBankUsers)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record CreateBankUserCommand(Guid BankId, CreateBankUserRequest Body) : ICommand<InvitedBankUserDto>, IBankScopedRequest, ITransactionalCommand;

/// <summary>
/// Deactivation takes effect on the user's next API request (the user is loaded from the database on every request), but
/// Cognito sessions and refresh tokens are not revoked (known limitation until Cognito integration).
/// </summary>
[RequirePermission(Permissions.ManageBankUsers)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record UpdateBankUserCommand(Guid BankId, Guid UserId, UpdateBankUserRequest Body) : ICommand<BankUserDto>, IBankScopedRequest, ITransactionalCommand;

[RequirePermission(Permissions.ManageBankUsers)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record DeleteBankUserCommand(Guid BankId, Guid UserId) : ICommand<BankUserDto>, IBankScopedRequest, ITransactionalCommand;

public sealed class CreateBankUserValidator : AbstractValidator<CreateBankUserCommand>
{
    public CreateBankUserValidator()
    {
        RuleFor(x => x.Body).NotNull();
        When(x => x.Body is not null, () =>
        {
            RuleFor(x => x.Body.Email).NotEmpty().MaximumLength(254).EmailAddress().OverridePropertyName("email");
            RuleFor(x => x.Body.FullName).NotEmpty().MaximumLength(200)
                .Must(n => !PanRedactor.ContainsPan(n)).WithMessage("Must not contain a card number.")
                .OverridePropertyName("fullName");
            RuleFor(x => x.Body.RoleId).NotNull().OverridePropertyName("roleId");
        });
    }
}

public sealed class UpdateBankUserValidator : AbstractValidator<UpdateBankUserCommand>
{
    public UpdateBankUserValidator()
    {
        RuleFor(x => x.Body).NotNull();
        When(x => x.Body is not null, () =>
        {
            RuleFor(x => x.Body)
                .Must(b => b.FullName is not null || b.RoleId is not null || b.IsActive is not null)
                .WithMessage("Send at least one of fullName, roleId or isActive.")
                .OverridePropertyName("body");
            RuleFor(x => x.Body.FullName!).NotEmpty().MaximumLength(200)
                .Must(n => !PanRedactor.ContainsPan(n)).WithMessage("Must not contain a card number.")
                .When(x => x.Body.FullName is not null)
                .OverridePropertyName("fullName");
        });
    }
}

internal sealed class CreateBankUserHandler(ChargebackDbContext db, IDapperQueryService queries, ICurrentUser currentUser, TimeProvider timeProvider)
    : IRequestHandler<CreateBankUserCommand, Result<InvitedBankUserDto>>
{
    public async Task<Result<InvitedBankUserDto>> Handle(CreateBankUserCommand request, CancellationToken cancellationToken)
    {
        if (!await db.Banks.AnyAsync(b => b.Id == request.BankId, cancellationToken))
        {
            return Errors.ResourceNotFound;
        }

        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == request.Body.RoleId, cancellationToken);
        if (!BankUserRules.IsAssignable(role))
        {
            return BankUserErrors.RoleNotAssignable;
        }

        var email = request.Body.Email!.Trim();
        var lowered = email.ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.DeletedAt == null && u.Email.ToLower() == lowered, cancellationToken))
        {
            return BankUserErrors.EmailInUse;
        }

        var user = new User
        {
            BankId = request.BankId,
            CognitoSub = "",
            Email = email,
            FullName = request.Body.FullName!.Trim(),
            UserType = UserType.Bank,
            RoleId = role!.Id,
            Status = UserStatuses.Disabled,
            InvitedAt = timeProvider.GetUtcNow(),
        };
        user.CognitoSub = UserStatuses.PendingInviteSub(user.Id);
        user.AddDomainEvent(new UserInvited { BankId = request.BankId, UserId = user.Id, RoleId = role.Id, InvitedBy = currentUser.UserId });
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);

        return new InvitedBankUserDto(
            (await BankUserReader.ReadAsync(queries, request.BankId, user.Id, cancellationToken))!,
            BankUserRules.NewInviteToken(),
            BankUserRules.InviteDelivery);
    }
}

internal sealed class UpdateBankUserHandler(ChargebackDbContext db, IDapperQueryService queries, ICurrentUser currentUser)
    : IRequestHandler<UpdateBankUserCommand, Result<BankUserDto>>
{
    public async Task<Result<BankUserDto>> Handle(UpdateBankUserCommand request, CancellationToken cancellationToken)
    {
        var user = await BankUserReader.FindLiveAsync(db, request.BankId, request.UserId, cancellationToken);
        if (user is null)
        {
            return Errors.ResourceNotFound;
        }

        var body = request.Body;
        var fromStatus = user.Status;
        var changed = new List<string>();
        if (body.RoleId is { } roleId && roleId != user.RoleId)
        {
            var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken);
            if (!BankUserRules.IsAssignable(role))
            {
                return BankUserErrors.RoleNotAssignable;
            }

            user.AddDomainEvent(new UserRoleChanged
            {
                BankId = request.BankId, UserId = user.Id, FromRoleId = user.RoleId, ToRoleId = roleId, ChangedBy = currentUser.UserId,
            });
            user.RoleId = roleId;
        }

        if (body.IsActive is { } active)
        {
            var target = active ? UserStatuses.Active : UserStatuses.Disabled;
            if (active && user.Status != UserStatuses.Active && !BankUserRules.CanActivate(user.CognitoSub))
            {
                return BankUserErrors.InvitePending;
            }

            if (target != user.Status)
            {
                user.Status = target;
                changed.Add("status");
            }
        }

        if (body.FullName is { } name && name.Trim() != user.FullName)
        {
            user.FullName = name.Trim();
            changed.Add("fullName");
        }

        if (changed.Count > 0)
        {
            user.AddDomainEvent(new UserUpdated
            {
                BankId = request.BankId, UserId = user.Id, ChangedFields = changed, FromStatus = fromStatus, ToStatus = user.Status, UpdatedBy = currentUser.UserId,
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return BankUserErrors.ConcurrentChange;
        }

        return (await BankUserReader.ReadAsync(queries, request.BankId, user.Id, cancellationToken))!;
    }
}

internal sealed class DeleteBankUserHandler(ChargebackDbContext db, IDapperQueryService queries, ICurrentUser currentUser, TimeProvider timeProvider)
    : IRequestHandler<DeleteBankUserCommand, Result<BankUserDto>>
{
    public async Task<Result<BankUserDto>> Handle(DeleteBankUserCommand request, CancellationToken cancellationToken)
    {
        if (!BankUserRules.CanDelete(request.UserId, currentUser.UserId))
        {
            return BankUserErrors.CannotDeleteSelf;
        }

        var user = await BankUserReader.FindLiveAsync(db, request.BankId, request.UserId, cancellationToken);
        if (user is null)
        {
            return Errors.ResourceNotFound;
        }

        user.DeletedAt = timeProvider.GetUtcNow();
        user.DeletedBy = currentUser.UserId;
        user.Status = UserStatuses.Disabled;
        user.AddDomainEvent(new UserDeleted { BankId = request.BankId, UserId = user.Id, DeletedBy = currentUser.UserId });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return BankUserErrors.ConcurrentChange;
        }

        return (await BankUserReader.ReadAsync(queries, request.BankId, user.Id, cancellationToken))!;
    }
}

internal static class BankUserReader
{
    /// <summary>A live (not removed) BANK user of the bank; anything else is reported as not found.</summary>
    public static Task<User?> FindLiveAsync(ChargebackDbContext db, Guid bankId, Guid userId, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(
            u => u.Id == userId && u.BankId == bankId && u.UserType == UserType.Bank && u.DeletedAt == null, cancellationToken);

    public static async Task<BankUserDto?> ReadAsync(IDapperQueryService db, Guid bankId, Guid userId, CancellationToken cancellationToken) =>
        (await db.QuerySingleOrDefaultAsync<BankUserRow>(
            $"SELECT {BankUserRow.Columns} FROM chargeback_diagram.users u WHERE u.id = @UserId AND u.bank_id = @BankId",
            new { UserId = userId, BankId = bankId },
            cancellationToken))?.ToDto();
}
