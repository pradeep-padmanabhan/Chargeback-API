using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Identity;
using Chargeback.IntegrationTests.Infrastructure;
using Chargeback.SharedKernel.Security;

namespace Chargeback.IntegrationTests.Security;

[Collection(PostgresCollection.Name)]
public sealed class CurrentUserTests(PostgresFixture fixture)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Bank_user_is_scoped_to_its_own_bank_only()
    {
        var bankId = await fixture.Data.CreateBankAsync();
        var user = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankId, Permissions.ViewCases);
        await fixture.Data.GrantScopeAsync(user.Id, await fixture.Data.CreateBankAsync()); // must be ignored for bank users

        var me = await GetMe(user.Sub);

        me.UserType.Should().Be(UserType.Bank);
        me.HomeBankId.Should().Be(bankId);
        me.BankScopes.Should().Equal(bankId);
        me.Permissions.Should().Equal(Permissions.ViewCases);
    }

    [Fact]
    public async Task Processor_scopes_are_explicit_and_time_bounded()
    {
        var current = await fixture.Data.CreateBankAsync();
        var expired = await fixture.Data.CreateBankAsync();
        var future = await fixture.Data.CreateBankAsync();
        await fixture.Data.CreateBankAsync(); // unrelated bank, never granted
        var user = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases);
        await fixture.Data.GrantScopeAsync(user.Id, current);
        await fixture.Data.GrantScopeAsync(user.Id, expired, DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1));
        await fixture.Data.GrantScopeAsync(user.Id, future, DateTimeOffset.UtcNow.AddDays(1));

        var me = await GetMe(user.Sub);

        me.HomeBankId.Should().BeNull();
        me.BankScopes.Should().Equal(current);
    }

    [Theory]
    [InlineData("PROCESSOR")]
    [InlineData("ADMIN")]
    public async Task Null_bank_id_without_scopes_grants_no_banks(string userType)
    {
        var user = await fixture.Data.CreateUserWithPermissionsAsync(userType, null, Permissions.ViewBanks);

        (await GetMe(user.Sub)).BankScopes.Should().BeEmpty();
    }

    [Fact]
    public async Task Inactive_role_and_inactive_permissions_grant_nothing()
    {
        var inactivePermission = "TEST_INACTIVE_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await fixture.Data.CreatePermissionAsync(inactivePermission, active: false);
        var activeRoleUser = await fixture.Data.CreateUserAsync(
            "PROCESSOR", await fixture.Data.CreateRoleAsync("PROCESSOR", [Permissions.ViewCases, inactivePermission]));
        var inactiveRoleUser = await fixture.Data.CreateUserAsync(
            "PROCESSOR", await fixture.Data.CreateRoleAsync("PROCESSOR", [Permissions.ViewCases], active: false));

        (await GetMe(activeRoleUser.Sub)).Permissions.Should().Equal(Permissions.ViewCases);
        (await GetMe(inactiveRoleUser.Sub)).Permissions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("DISABLED")]
    [InlineData("LOCKED")]
    public async Task Non_active_user_is_forbidden(string status)
    {
        var user = await fixture.Data.CreateUserAsync("PROCESSOR", await fixture.Data.CreateRoleAsync("PROCESSOR", []), status: status);

        await AssertProblem(user.Sub, HttpStatusCode.Forbidden, "USER_NOT_ACTIVE");
    }

    [Fact]
    public async Task Unknown_identity_is_forbidden()
    {
        await AssertProblem("sub-not-provisioned", HttpStatusCode.Forbidden, "USER_NOT_PROVISIONED");
    }

    [Fact]
    public async Task Role_type_must_match_user_type()
    {
        var user = await fixture.Data.CreateUserAsync("PROCESSOR", await fixture.Data.CreateRoleAsync("BANK", [Permissions.ViewCases]));

        await AssertProblem(user.Sub, HttpStatusCode.Forbidden, "USER_ROLE_MISMATCH");
    }

    [Fact]
    public async Task Missing_credentials_are_unauthorized()
    {
        using var client = fixture.Factory.CreateClientFor(null);

        (await client.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<CurrentUserDto> GetMe(string sub)
    {
        using var client = fixture.Factory.CreateClientFor(sub);
        var response = await client.GetAsync("/api/v1/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CurrentUserDto>(Json))!;
    }

    private async Task AssertProblem(string sub, HttpStatusCode status, string code)
    {
        using var client = fixture.Factory.CreateClientFor(sub);
        var response = await client.GetAsync("/api/v1/me");

        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be(code);
        problem.GetProperty("traceId").GetString().Should().Be(response.Headers.GetValues("X-Correlation-Id").Single());
        problem.TryGetProperty("correlationId", out _).Should().BeFalse("traceId replaces correlationId (common guide §3.4)");
    }
}
