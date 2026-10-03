using System.Reflection;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.ClientPortal;
using Chargeback.Api.Features.ClientPortal.Contracts;
using Chargeback.Infrastructure.Security;
using Chargeback.Infrastructure.Support;
using Chargeback.SharedKernel.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chargeback.UnitTests.ClientPortal;

public sealed class BankScopeResolutionTests
{
    private static readonly Guid Own = Guid.NewGuid();
    private static readonly Guid Granted = Guid.NewGuid();

    [Fact]
    public void Bank_user_with_null_bank_id_gets_no_bank() =>
        BankScopeResolution.For(UserType.Bank, null, [Granted]).Should().BeEmpty("NULL bank_id never implies access to any bank");

    [Fact]
    public void Bank_user_gets_only_its_own_bank_and_ignores_grants() =>
        BankScopeResolution.For(UserType.Bank, Own, [Granted]).Should().Equal(Own);

    [Theory]
    [InlineData(UserType.Processor)]
    [InlineData(UserType.Admin)]
    public void Processor_and_admin_get_only_explicit_grants(UserType type)
    {
        BankScopeResolution.For(type, null, [Granted]).Should().Equal(Granted);
        BankScopeResolution.For(type, null, []).Should().BeEmpty("NULL bank_id with no grants is no access, not all banks");
    }
}

public sealed class CaseMessageRulesTests
{
    [Theory]
    [InlineData(UserType.Bank, "BANK", MessageSenderTypes.BankUser)]
    [InlineData(UserType.Processor, "PROCESSOR", MessageSenderTypes.Analyst)]
    [InlineData(UserType.Admin, "PROCESSOR", MessageSenderTypes.Analyst)]
    public void Sender_type_comes_from_the_caller(UserType caller, string stored, string api)
    {
        CaseMessageRules.StoredSenderType(caller).Should().Be(stored);
        CaseMessageRules.ApiSenderType(stored).Should().Be(api);
    }

    [Theory]
    [InlineData("Please see the attached receipt.", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("card 4111 1111 1111 1111", false)]
    public void Message_body_shape(string? body, bool valid)
    {
        new PostPortalMessageValidator().Validate(new PostPortalMessageCommand(Guid.NewGuid(), new PostMessageRequest(body))).IsValid.Should().Be(valid);
        new PostCaseMessageValidator().Validate(new PostCaseMessageCommand(Guid.NewGuid(), new PostMessageRequest(body))).IsValid.Should().Be(valid);
    }

    [Theory]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public void Message_body_is_at_most_2000_characters(int length, bool valid) =>
        new PostPortalMessageValidator().Validate(new PostPortalMessageCommand(Guid.NewGuid(), new PostMessageRequest(new string('m', length))))
            .IsValid.Should().Be(valid);

    [Theory]
    [InlineData("Help", "Where is my refund?", true)]
    [InlineData("", "body", false)]
    [InlineData("Help", "", false)]
    [InlineData("Help", "card 4111111111111111", false)]
    public void Support_ticket_shape(string subject, string body, bool valid) =>
        new CreateSupportTicketValidator().Validate(new CreateSupportTicketCommand(Guid.NewGuid(), new SupportTicketRequest(subject, body)))
            .IsValid.Should().Be(valid);
}

public sealed class ClientPortalGuardTests
{
    public static TheoryData<Type> PortalRequests() =>
    [
        typeof(ListPortalCasesQuery), typeof(GetPortalCaseQuery), typeof(ListPortalMessagesQuery), typeof(PostPortalMessageCommand),
        typeof(CreateSupportTicketCommand),
    ];

    [Theory]
    [MemberData(nameof(PortalRequests))]
    public void Portal_operations_are_bank_users_only_with_no_permission_code(Type request)
    {
        request.GetCustomAttribute<RequirePermissionAttribute>().Should().BeNull("BANK users hold no permissions (guide §3.1)");
        request.GetCustomAttribute<AllowAnyPlatformUserAttribute>().Should().NotBeNull();
        request.GetCustomAttribute<RestrictToUserTypesAttribute>()!.UserTypes.Should().Equal(UserType.Bank);
    }

    [Theory]
    [InlineData(typeof(ListCaseMessagesQuery))]
    [InlineData(typeof(PostCaseMessageCommand))]
    public void Internal_thread_needs_view_cases_and_processor_or_admin(Type request)
    {
        request.GetCustomAttribute<RequirePermissionAttribute>()!.Permission.Should().Be(Permissions.ViewCases);
        request.GetCustomAttribute<RestrictToUserTypesAttribute>()!.UserTypes.Should().BeEquivalentTo([UserType.Processor, UserType.Admin]);
    }

    [Fact]
    public async Task Zendesk_stub_returns_a_stub_ticket_id()
    {
        var client = new KnownLimitationZendeskClient(NullLogger<KnownLimitationZendeskClient>.Instance);

        var first = await client.CreateTicketAsync(Guid.NewGuid(), Guid.NewGuid(), "s", "b", CancellationToken.None);
        var second = await client.CreateTicketAsync(Guid.NewGuid(), Guid.NewGuid(), "s", "b", CancellationToken.None);

        first.Should().MatchRegex("^STUB-[0-9a-f-]{36}$");
        second.Should().NotBe(first);
    }
}
