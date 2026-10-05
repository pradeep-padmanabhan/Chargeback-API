using System.Net;
using System.Net.Http.Json;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Admin.Contracts;
using Chargeback.IntegrationTests.Infrastructure;
using Chargeback.SharedKernel.Paging;

namespace Chargeback.IntegrationTests.Security;

/// <summary>Implemented read endpoints: endpoint → MediatR → handler → PostgreSQL with scope enforcement.</summary>
[Collection(PostgresCollection.Name)]
public sealed class BankScopeTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Bank_list_contains_only_scoped_banks()
    {
        var a = await fixture.Data.CreateBankAsync();
        var c = await fixture.Data.CreateBankAsync();
        await fixture.Data.CreateBankAsync(); // not granted
        var processor = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewBankUsers);
        await fixture.Data.GrantScopeAsync(processor.Id, a);
        await fixture.Data.GrantScopeAsync(processor.Id, c);

        var page = await GetOk<PagedResult<BankDto>>(processor.Sub, "/api/v1/admin/banks?pageSize=100");

        page.Items.Select(b => b.Id).Should().BeEquivalentTo([a, c]);
        page.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task Bank_users_cannot_use_admin_bank_endpoints()
    {
        var own = await fixture.Data.CreateBankAsync();
        var bankUser = await fixture.Data.CreateUserWithPermissionsAsync("BANK", own, Permissions.ViewBankUsers, Permissions.ManageBankUsers);

        (await Get(bankUser.Sub, "/api/v1/admin/banks")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "a bank-admin role is deferred");
        (await Get(bankUser.Sub, $"/api/v1/admin/banks/{own}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Get(bankUser.Sub, $"/api/v1/admin/banks/{own}/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Bank_outside_scope_is_404_and_missing_permission_is_403()
    {
        var own = await fixture.Data.CreateBankAsync();
        var other = await fixture.Data.CreateBankAsync();
        var processor = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewBankUsers);
        await fixture.Data.GrantScopeAsync(processor.Id, own);
        var noPermission = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases);
        await fixture.Data.GrantScopeAsync(noPermission.Id, own);

        (await Get(processor.Sub, $"/api/v1/admin/banks/{own}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Get(processor.Sub, $"/api/v1/admin/banks/{other}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Get(processor.Sub, $"/api/v1/admin/banks/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Get(noPermission.Sub, $"/api/v1/admin/banks/{own}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Viewing_bank_users_needs_permission_and_scope()
    {
        var bank = await fixture.Data.CreateBankAsync();
        var member = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bank);
        var scoped = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewBankUsers);
        await fixture.Data.GrantScopeAsync(scoped.Id, bank);
        var unscoped = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewBankUsers);
        var noPermission = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewBanks);
        await fixture.Data.GrantScopeAsync(noPermission.Id, bank);

        var page = await GetOk<PagedResult<BankUserDto>>(scoped.Sub, $"/api/v1/admin/banks/{bank}/users");

        page.Items.Select(u => u.Id).Should().Equal(member.Id);
        (await Get(unscoped.Sub, $"/api/v1/admin/banks/{bank}/users")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Get(noPermission.Sub, $"/api/v1/admin/banks/{bank}/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Permission_catalogue_requires_role_management_and_processor_or_admin()
    {
        var admin = await fixture.Data.CreateUserWithPermissionsAsync("ADMIN", null, Permissions.ManageRoles);
        var bankUser = await fixture.Data.CreateUserWithPermissionsAsync("BANK", await fixture.Data.CreateBankAsync(), Permissions.ManageRoles);

        var permissions = await GetOk<List<PermissionDto>>(admin.Sub, "/api/v1/admin/permissions");

        permissions.Select(p => p.Name).Should().Contain(Permissions.Seeded);
        (await Get(bankUser.Sub, "/api/v1/admin/permissions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<HttpResponseMessage> Get(string sub, string url)
    {
        using var client = fixture.Factory.CreateClientFor(sub);
        return await client.GetAsync(url);
    }

    private async Task<T> GetOk<T>(string sub, string url)
    {
        var response = await Get(sub, url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(CurrentUserTests.Json))!;
    }
}
