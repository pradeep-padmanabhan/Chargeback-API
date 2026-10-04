using Chargeback.Api;
using Chargeback.Api.Common.Behaviors;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Admin;
using Chargeback.Api.Features.Cases.CreateCase;
using Chargeback.Api.Features.Cases.GetCases;
using Chargeback.Api.Features.Identity;
using Chargeback.Infrastructure.Persistence;
using Chargeback.SharedKernel.Results;
using MediatR;

namespace Chargeback.UnitTests.Behaviors;

public sealed class RlsSetupBehaviorTests
{
    [Theory]
    [InlineData(typeof(ListBankUsersQuery), DatabaseScopeMode.Banks)]          // IBankScopedRequest
    [InlineData(typeof(GetCaseQuery), DatabaseScopeMode.Banks)]                // IResourceScopedRequest
    [InlineData(typeof(ListCasesQuery), DatabaseScopeMode.Banks)]              // IScopeFilteredRequest
    [InlineData(typeof(ListRolesQuery), DatabaseScopeMode.None)]               // [NotBankScoped]
    [InlineData(typeof(GetCurrentUserQuery), DatabaseScopeMode.None)]
    [InlineData(typeof(CreateCaseForDisputeCommand), DatabaseScopeMode.System)] // [SystemOperation]
    public void Request_types_map_to_the_approved_scope(Type request, DatabaseScopeMode expected) =>
        RlsSetupBehavior<object, Result>.ScopeFor(RequestAuthorizationMetadata.For(request)).Should().Be(expected);

    [Fact]
    public void Every_bank_owned_request_gets_a_scope()
    {
        var requests = typeof(ApiServiceRegistration).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>)))
            .Select(RequestAuthorizationMetadata.For)
            .ToArray();

        requests.Should().NotBeEmpty();
        requests.Where(m => m.Scope != ScopeRule.NotBankScoped)
            .Should().OnlyContain(m => RlsSetupBehavior<object, Result>.ScopeFor(m) != DatabaseScopeMode.None, "only [NotBankScoped] requests run without a scope");
    }

    [Fact]
    public async Task Bank_scoped_request_passes_exactly_the_callers_banks()
    {
        var scope = Substitute.For<IDatabaseScope>();
        var user = new FakeCurrentUser();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        user.Scopes.UnionWith([a, b]);

        await new RlsSetupBehavior<BankScopedTestQuery, Result<string>>(scope, user)
            .Handle(new BankScopedTestQuery(a), _ => Task.FromResult(Result.Success("ok")), CancellationToken.None);

        await scope.Received(1).UseBanksAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.ToHashSet().SetEquals(new[] { a, b })), Arg.Any<CancellationToken>());
        await scope.DidNotReceive().UseSystemAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Caller_without_banks_gets_an_empty_scope_not_all_banks()
    {
        var scope = Substitute.For<IDatabaseScope>();

        await new RlsSetupBehavior<BankScopedTestQuery, Result<string>>(scope, new FakeCurrentUser())
            .Handle(new BankScopedTestQuery(Guid.NewGuid()), _ => Task.FromResult(Result.Success("ok")), CancellationToken.None);

        await scope.Received(1).UseBanksAsync(Arg.Is<IEnumerable<Guid>>(ids => !ids.Any()), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Not_bank_scoped_request_sets_no_scope()
    {
        var scope = Substitute.For<IDatabaseScope>();

        await new RlsSetupBehavior<ListRolesQuery, Result<IReadOnlyList<Chargeback.Api.Features.Admin.Contracts.RoleDto>>>(scope, new FakeCurrentUser())
            .Handle(new ListRolesQuery(), _ => Task.FromResult(Result.Success<IReadOnlyList<Chargeback.Api.Features.Admin.Contracts.RoleDto>>([])), CancellationToken.None);

        await scope.DidNotReceive().UseBanksAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
        await scope.DidNotReceive().UseSystemAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(DatabaseScopeMode.None, "", "")]
    [InlineData(DatabaseScopeMode.System, "system", "")]
    public void Session_values_without_banks(DatabaseScopeMode mode, string scope, string ids)
    {
        DatabaseScopeSql.ScopeValue(mode).Should().Be(scope);
        DatabaseScopeSql.BankIdsValue(mode, [Guid.NewGuid()]).Should().Be(ids, "bank ids are only sent in bank scope");
    }

    [Fact]
    public void Bank_ids_are_sent_as_a_uuid_array_literal()
    {
        var a = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var b = Guid.Parse("22222222-2222-2222-2222-222222222222");

        DatabaseScopeSql.BankIdsValue(DatabaseScopeMode.Banks, [a, b]).Should().Be("{11111111-1111-1111-1111-111111111111,22222222-2222-2222-2222-222222222222}");
        DatabaseScopeSql.BankIdsValue(DatabaseScopeMode.Banks, []).Should().Be("{}");
    }
}
