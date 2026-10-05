using Chargeback.Api.Common.Behaviors;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chargeback.UnitTests.Behaviors;

public sealed class AuthorizationBehaviorTests
{
    private readonly FakeCurrentUser _user = new();
    private readonly IResourceBankResolver _resolver = Substitute.For<IResourceBankResolver>();
    private bool _handlerCalled;

    [Fact]
    public async Task Unauthenticated_caller_is_rejected_before_handler()
    {
        _user.LoadResult = Result.Failure(Errors.Unauthenticated);

        var result = await Run(new AnyUserTestQuery());

        result.Error.Should().Be(Errors.Unauthenticated);
        _handlerCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Missing_permission_is_forbidden()
    {
        var bankId = Guid.NewGuid();
        _user.Scopes.Add(bankId);

        var result = await Run(new BankScopedTestQuery(bankId));

        result.Error.Code.Should().Be(Errors.PermissionDenied.Code);
        _handlerCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Permission_and_bank_scope_allow_handler()
    {
        var bankId = Guid.NewGuid();
        _user.PermissionSet.Add("VIEW_CASES");
        _user.Scopes.Add(bankId);

        var result = await Run(new BankScopedTestQuery(bankId));

        result.IsSuccess.Should().BeTrue();
        _handlerCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Bank_outside_scope_is_reported_as_not_found()
    {
        _user.PermissionSet.Add("VIEW_CASES");
        _user.Scopes.Add(Guid.NewGuid());

        var result = await Run(new BankScopedTestQuery(Guid.NewGuid()));

        result.Error.Should().Be(Errors.ResourceNotFound);
        _handlerCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Resource_of_another_bank_is_not_found()
    {
        var own = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        _user.PermissionSet.Add("VIEW_CASES");
        _user.Scopes.Add(own);
        _resolver.ResolveBankIdAsync(ScopedResourceKind.Case, caseId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());

        var result = await Run(new ResourceScopedTestQuery(caseId));

        result.Error.Should().Be(Errors.ResourceNotFound);
    }

    [Fact]
    public async Task Missing_resource_and_foreign_resource_are_indistinguishable()
    {
        _user.PermissionSet.Add("VIEW_CASES");
        _user.Scopes.Add(Guid.NewGuid());
        _resolver.ResolveBankIdAsync(default, default, default).ReturnsForAnyArgs((Guid?)null);

        var result = await Run(new ResourceScopedTestQuery(Guid.NewGuid()));

        result.Error.Should().Be(Errors.ResourceNotFound);
    }

    [Fact]
    public async Task Resource_in_scope_allows_handler()
    {
        var own = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        _user.PermissionSet.Add("VIEW_CASES");
        _user.Scopes.Add(own);
        _resolver.ResolveBankIdAsync(ScopedResourceKind.Case, caseId, Arg.Any<CancellationToken>()).Returns(own);

        (await Run(new ResourceScopedTestQuery(caseId))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task User_type_restriction_is_enforced()
    {
        _user.UserType = UserType.Processor;
        _user.PermissionSet.Add("VIEW_CASES");

        var result = await Run(new BankUsersOnlyTestQuery());

        result.Error.Should().Be(Errors.UserTypeNotAllowed);
    }

    [Fact]
    public async Task Null_home_bank_grants_no_bank_access()
    {
        _user.UserType = UserType.Admin;
        _user.HomeBankId = null;
        _user.PermissionSet.Add("VIEW_CASES");

        var result = await Run(new BankScopedTestQuery(Guid.NewGuid()));

        result.Error.Should().Be(Errors.ResourceNotFound);
    }

    [Theory]
    [InlineData(typeof(UndeclaredTestQuery))]
    [InlineData(typeof(DoublePermissionTestQuery))]
    [InlineData(typeof(DoubleScopeTestQuery))]
    public void Invalid_declarations_are_detected(Type requestType)
    {
        RequestAuthorizationMetadata.For(requestType).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Undeclared_request_is_denied_by_default()
    {
        _user.PermissionSet.Add("VIEW_CASES");

        var result = await Run(new UndeclaredTestQuery());

        result.Error.Should().Be(Errors.AuthorizationMisconfigured);
        _user.LoadCalls.Should().Be(0);
        _handlerCalled.Should().BeFalse();
    }

    private Task<Result<string>> Run<TRequest>(TRequest request)
        where TRequest : IRequest<Result<string>>
    {
        var behavior = new AuthorizationBehavior<TRequest, Result<string>>(
            _user, _user, _resolver, NullLogger<AuthorizationBehavior<TRequest, Result<string>>>.Instance);
        return behavior.Handle(request, _ =>
        {
            _handlerCalled = true;
            return Task.FromResult(Result.Success("ok"));
        }, CancellationToken.None);
    }
}
