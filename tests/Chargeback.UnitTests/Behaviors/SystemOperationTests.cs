using Chargeback.Api.Common.Behaviors;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chargeback.UnitTests.Behaviors;

[SystemOperation("test workflow step")]
[NotBankScoped("test")]
public sealed record SystemTestCommand : ICommand;

[SystemOperation("x")]
[RequirePermission("VIEW_CASES")]
[NotBankScoped("x")]
public sealed record SystemAndPermissionTestCommand : ICommand;

[SystemOperation("x")]
[RestrictToUserTypes(UserType.Admin)]
[NotBankScoped("x")]
public sealed record SystemWithUserTypeTestCommand : ICommand;

public sealed class SystemOperationTests
{
    private readonly FakeCurrentUser _user = new();

    private Task<Result> Run(SystemTestCommand command) =>
        new AuthorizationBehavior<SystemTestCommand, Result>(_user, _user, Substitute.For<IResourceBankResolver>(), NullLogger<AuthorizationBehavior<SystemTestCommand, Result>>.Instance)
            .Handle(command, _ => Task.FromResult(Result.Success()), CancellationToken.None);

    [Fact]
    public async Task System_operation_is_forbidden_to_users_even_with_every_permission()
    {
        _user.UserType = UserType.Admin;
        _user.PermissionSet.UnionWith(Permissions.Seeded.Concat(Permissions.Proposed));

        var result = await Run(new SystemTestCommand());

        result.Error.Should().Be(Errors.SystemOperationOnly);
        _user.LoadCalls.Should().Be(0);
    }

    [Fact]
    public async Task System_operation_runs_inside_system_execution_only()
    {
        using (SystemExecution.Begin("unit-test"))
        {
            (await Run(new SystemTestCommand())).IsSuccess.Should().BeTrue();
        }

        SystemExecution.IsActive.Should().BeFalse();
        (await Run(new SystemTestCommand())).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(typeof(SystemAndPermissionTestCommand))]
    [InlineData(typeof(SystemWithUserTypeTestCommand))]
    public void System_operation_cannot_be_combined_with_user_rules(Type type)
    {
        RequestAuthorizationMetadata.For(type).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Unavailable_maps_to_503()
    {
        ResultHttpMapper.StatusCodeFor(ErrorType.Unavailable).Should().Be(503);
    }
}
