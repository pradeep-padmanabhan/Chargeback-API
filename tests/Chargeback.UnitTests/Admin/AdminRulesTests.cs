using System.Reflection;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Admin;
using Chargeback.Api.Features.Admin.Contracts;
using Chargeback.Infrastructure.Persistence.Entities;
using Chargeback.SharedKernel.Security;

namespace Chargeback.UnitTests.Admin;

public sealed class BankUserRulesTests
{
    [Theory]
    [InlineData(UserType.Bank, true, true)]
    [InlineData(UserType.Bank, false, false)]       // inactive role
    [InlineData(UserType.Processor, true, false)]   // role type must match the BANK user type
    [InlineData(UserType.Admin, true, false)]
    public void Only_active_bank_roles_are_assignable(UserType roleType, bool active, bool assignable) =>
        BankUserRules.IsAssignable(new Role { Name = "r", RoleType = roleType, IsActive = active }).Should().Be(assignable);

    [Fact]
    public void Missing_role_is_not_assignable() => BankUserRules.IsAssignable(null).Should().BeFalse();

    [Fact]
    public void Invited_users_cannot_be_activated_until_linked()
    {
        var id = Guid.NewGuid();

        BankUserRules.CanActivate(UserStatuses.PendingInviteSub(id)).Should().BeFalse();
        BankUserRules.CanActivate("cognito-sub-123").Should().BeTrue();
        UserStatuses.PendingInviteSub(id).Should().Be($"pending-invite:{id:N}");
    }

    [Fact]
    public void Nobody_deletes_themselves()
    {
        var me = Guid.NewGuid();

        BankUserRules.CanDelete(me, me).Should().BeFalse();
        BankUserRules.CanDelete(Guid.NewGuid(), me).Should().BeTrue();
    }

    [Fact]
    public void Invite_tokens_are_marked_placeholders_and_unique()
    {
        var a = BankUserRules.NewInviteToken();

        a.Should().StartWith("KNOWN_LIMITATION_INVITE_EMAIL_");
        BankUserRules.NewInviteToken().Should().NotBe(a);
    }
}

public sealed class BankUserValidatorTests
{
    [Theory]
    [InlineData("ana@example.test", "Ana Lima", true, true)]
    [InlineData("not-an-email", "Ana", true, false)]
    [InlineData("", "Ana", true, false)]
    [InlineData("ana@example.test", "", true, false)]
    [InlineData("ana@example.test", "card 4111111111111111", true, false)]
    [InlineData("ana@example.test", "Ana", false, false)]   // role required
    public void Invite_shape(string email, string name, bool withRole, bool valid) =>
        new CreateBankUserValidator()
            .Validate(new CreateBankUserCommand(Guid.NewGuid(), new CreateBankUserRequest(email, name, withRole ? Guid.NewGuid() : null)))
            .IsValid.Should().Be(valid);

    [Theory]
    [InlineData("New Name", false, null, true)]
    [InlineData(null, true, null, true)]
    [InlineData(null, false, false, true)]
    [InlineData(null, false, null, false)]                  // nothing to change
    [InlineData("", false, null, false)]
    public void Update_needs_at_least_one_change(string? name, bool withRole, bool? isActive, bool valid) =>
        new UpdateBankUserValidator()
            .Validate(new UpdateBankUserCommand(Guid.NewGuid(), Guid.NewGuid(), new UpdateBankUserRequest(name, withRole ? Guid.NewGuid() : null, isActive)))
            .IsValid.Should().Be(valid);
}

/// <summary>The declared guard of every Admin & Configuration operation in this slice.</summary>
public sealed class AdminPermissionGuardTests
{
    public static TheoryData<Type, string?, bool> Guards() => new()
    {
        { typeof(ListBanksQuery), Permissions.ViewBankUsers, true },
        { typeof(GetBankQuery), Permissions.ViewBankUsers, true },
        { typeof(ListBankUsersQuery), Permissions.ViewBankUsers, true },
        { typeof(CreateBankUserCommand), Permissions.ManageBankUsers, true },
        { typeof(UpdateBankUserCommand), Permissions.ManageBankUsers, true },
        { typeof(DeleteBankUserCommand), Permissions.ManageBankUsers, true },
        { typeof(ListRolesQuery), null, false },             // any platform user
    };

    [Theory]
    [MemberData(nameof(Guards))]
    public void Operation_declares_the_approved_guard(Type request, string? permission, bool processorOrAdminOnly)
    {
        request.GetCustomAttribute<RequirePermissionAttribute>()?.Permission.Should().Be(permission);
        if (permission is null)
        {
            request.GetCustomAttribute<RequirePermissionAttribute>().Should().BeNull();
            request.GetCustomAttribute<AllowAnyPlatformUserAttribute>().Should().NotBeNull();
        }

        var restricted = request.GetCustomAttribute<RestrictToUserTypesAttribute>();
        if (processorOrAdminOnly)
        {
            restricted!.UserTypes.Should().BeEquivalentTo([UserType.Processor, UserType.Admin], "a bank-admin role is deferred");
        }
        else
        {
            restricted.Should().BeNull();
        }
    }

    [Fact]
    public void Bank_user_operations_are_bank_scoped()
    {
        new[] { typeof(GetBankQuery), typeof(ListBankUsersQuery), typeof(CreateBankUserCommand), typeof(UpdateBankUserCommand), typeof(DeleteBankUserCommand) }
            .Should().OnlyContain(t => typeof(IBankScopedRequest).IsAssignableFrom(t));
    }

    [Fact]
    public void Invite_response_never_claims_delivery() =>
        BankUserRules.InviteDelivery.Should().StartWith("NOT_SENT").And.Contain("KNOWN_LIMITATION_INVITE_EMAIL_");

    [Fact]
    public void Invite_payload_type_is_unchanged() => typeof(InvitedBankUserDto).GetProperty("InviteToken").Should().NotBeNull();
}
