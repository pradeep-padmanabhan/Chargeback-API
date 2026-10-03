using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Admin.Contracts;
using Chargeback.IntegrationTests.Infrastructure;
using Chargeback.SharedKernel.Paging;
using Chargeback.SharedKernel.Security;

namespace Chargeback.IntegrationTests.Security;

/// <summary>Admin & Configuration: bank and bank-user management, scope isolation, audit trail and role listing.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AdminUserManagementTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("PROCESSOR")]
    [InlineData("ADMIN")]
    public async Task Manager_invites_updates_and_removes_a_bank_user_with_an_audit_trail(string managerType)
    {
        var bank = await fixture.Data.CreateBankAsync();
        var manager = await Manager(managerType, bank);
        var bankUserRole = await RoleId("Bank User");

        // Invite.
        var invite = await Send(manager.Sub, HttpMethod.Post, $"/api/v1/admin/banks/{bank}/users", Invite($"ana-{Guid.NewGuid():N}@example.test", "Ana Lima", bankUserRole));
        invite.StatusCode.Should().Be(HttpStatusCode.Created, await invite.Content.ReadAsStringAsync());
        var invited = (await invite.Content.ReadFromJsonAsync<InvitedBankUserDto>(CurrentUserTests.Json))!;
        invited.User.Status.Should().Be("DISABLED", "invited users stay disabled until identity linking exists");
        invited.User.UserType.Should().Be(UserType.Bank);
        invited.User.RoleId.Should().Be(bankUserRole);
        invited.User.InvitedAt.Should().NotBeNull();
        invited.InviteToken.Should().StartWith("KNOWN_LIMITATION_INVITE_EMAIL_");
        invited.InviteDelivery.Should().StartWith("NOT_SENT");
        var userId = invited.User.Id;
        (await fixture.Data.QueryScalarAsync<string>("SELECT cognito_sub FROM chargeback_diagram.users WHERE id = @userId", new { userId }))
            .Should().Be($"pending-invite:{userId:N}");
        (await Count("SELECT count(*) FROM chargeback_diagram.domain_events WHERE event_data::text LIKE '%' || @token || '%'", invited.InviteToken))
            .Should().Be(0, "the invite token is never stored");

        (await GetOk<PagedResult<BankUserDto>>(manager.Sub, $"/api/v1/admin/banks/{bank}/users")).Items.Should().ContainSingle(u => u.Id == userId);

        // Update: name, then role (audited), then activation (refused while the invite is pending).
        (await Send(manager.Sub, HttpMethod.Patch, $"/api/v1/admin/banks/{bank}/users/{userId}", """{"fullName":"Ana L. Lima"}"""))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var otherBankRole = await fixture.Data.QueryScalarAsync<Guid>(
            "INSERT INTO chargeback_diagram.roles(name, role_type) VALUES (@name, 'BANK') RETURNING id", new { name = "SYN bank role " + Guid.NewGuid().ToString("N")[..8] });
        var roleChange = await Send(manager.Sub, HttpMethod.Patch, $"/api/v1/admin/banks/{bank}/users/{userId}", $$"""{"roleId":"{{otherBankRole}}"}""");
        roleChange.StatusCode.Should().Be(HttpStatusCode.OK, await roleChange.Content.ReadAsStringAsync());
        (await roleChange.Content.ReadFromJsonAsync<BankUserDto>(CurrentUserTests.Json))!.RoleId.Should().Be(otherBankRole);
        var activate = await Send(manager.Sub, HttpMethod.Patch, $"/api/v1/admin/banks/{bank}/users/{userId}", """{"isActive":true}""");
        activate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await activate.Content.ReadAsStringAsync()).Should().Contain("INVITE_PENDING");

        // Remove (soft delete).
        (await Send(manager.Sub, HttpMethod.Delete, $"/api/v1/admin/banks/{bank}/users/{userId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetOk<PagedResult<BankUserDto>>(manager.Sub, $"/api/v1/admin/banks/{bank}/users")).Items.Should().NotContain(u => u.Id == userId);
        var row = JsonDocument.Parse(await fixture.Data.QueryScalarAsync<string>(
            "SELECT json_build_object('status', status, 'deletedAt', deleted_at, 'deletedBy', deleted_by)::text FROM chargeback_diagram.users WHERE id = @userId",
            new { userId })).RootElement;
        row.GetProperty("status").GetString().Should().Be("DISABLED");
        row.GetProperty("deletedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
        row.GetProperty("deletedBy").GetGuid().Should().Be(manager.Id);
        (await Send(manager.Sub, HttpMethod.Delete, $"/api/v1/admin/banks/{bank}/users/{userId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(manager.Sub, HttpMethod.Patch, $"/api/v1/admin/banks/{bank}/users/{userId}", """{"fullName":"x"}""")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Audit trail, in order; the role change records both roles.
        (await AuditTrail(userId)).Select(e => e.EventType).Should().Equal("user.invited", "user.updated", "user.role.changed", "user.deleted");
        var roleEvent = (await AuditTrail(userId)).Single(e => e.EventType == "user.role.changed").Data;
        roleEvent.GetProperty("fromRoleId").GetGuid().Should().Be(bankUserRole);
        roleEvent.GetProperty("toRoleId").GetGuid().Should().Be(otherBankRole);
        roleEvent.GetProperty("changedBy").GetGuid().Should().Be(manager.Id);
    }

    [Fact]
    public async Task Deactivation_applies_from_the_users_next_request()
    {
        var bank = await fixture.Data.CreateBankAsync();
        var manager = await Manager("PROCESSOR", bank);
        var member = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bank);
        await fixture.Data.ExecuteAsync("UPDATE chargeback_diagram.users SET role_id = @role WHERE id = @id", new { role = await RoleId("Bank User"), id = member.Id });
        (await Send(member.Sub, HttpMethod.Get, "/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        var deactivate = await Send(manager.Sub, HttpMethod.Patch, $"/api/v1/admin/banks/{bank}/users/{member.Id}", """{"isActive":false}""");

        deactivate.StatusCode.Should().Be(HttpStatusCode.OK, await deactivate.Content.ReadAsStringAsync());
        (await deactivate.Content.ReadFromJsonAsync<BankUserDto>(CurrentUserTests.Json))!.Status.Should().Be("DISABLED");
        var me = await Send(member.Sub, HttpMethod.Get, "/api/v1/me");
        me.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await me.Content.ReadAsStringAsync()).Should().Contain("USER_NOT_ACTIVE");
        (await Send(manager.Sub, HttpMethod.Patch, $"/api/v1/admin/banks/{bank}/users/{member.Id}", """{"isActive":true}""")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(member.Sub, HttpMethod.Get, "/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Invites_and_updates_are_validated()
    {
        var bank = await fixture.Data.CreateBankAsync();
        var manager = await Manager("PROCESSOR", bank);
        var email = $"dup-{Guid.NewGuid():N}@example.test";
        var bankUserRole = await RoleId("Bank User");

        (await Send(manager.Sub, HttpMethod.Post, $"/api/v1/admin/banks/{bank}/users", Invite(email, "First", bankUserRole))).StatusCode.Should().Be(HttpStatusCode.Created);
        var duplicate = await Send(manager.Sub, HttpMethod.Post, $"/api/v1/admin/banks/{bank}/users", Invite(email.ToUpperInvariant(), "Second", bankUserRole));
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await duplicate.Content.ReadAsStringAsync()).Should().Contain("USER_EMAIL_IN_USE");

        foreach (var role in new[] { await RoleId("Analyst"), Guid.NewGuid() })
        {
            var refused = await Send(manager.Sub, HttpMethod.Post, $"/api/v1/admin/banks/{bank}/users", Invite($"x-{Guid.NewGuid():N}@example.test", "X", role));
            refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("ROLE_NOT_ASSIGNABLE");
        }

        (await Send(manager.Sub, HttpMethod.Post, $"/api/v1/admin/banks/{bank}/users", Invite("not-an-email", "X", bankUserRole))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var member = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bank);
        (await Send(manager.Sub, HttpMethod.Patch, $"/api/v1/admin/banks/{bank}/users/{member.Id}", "{}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(manager.Sub, HttpMethod.Patch, $"/api/v1/admin/banks/{bank}/users/{member.Id}", $$"""{"roleId":"{{await RoleId("Admin")}}"}""")).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);

        var self = await Send(manager.Sub, HttpMethod.Delete, $"/api/v1/admin/banks/{bank}/users/{manager.Id}");
        self.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await self.Content.ReadAsStringAsync()).Should().Contain("CANNOT_DELETE_SELF");
    }

    [Fact]
    public async Task Processor_sees_and_manages_only_banks_in_its_explicit_scope()
    {
        var bankA = await fixture.Data.CreateBankAsync();
        var bankB = await fixture.Data.CreateBankAsync();
        var userB = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bankB);
        var scopedToA = await Manager("PROCESSOR", bankA);
        var role = await RoleId("Bank User");

        // Bank B is outside scope: 404 everywhere (existence not revealed).
        (await Send(scopedToA.Sub, HttpMethod.Get, $"/api/v1/admin/banks/{bankB}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(scopedToA.Sub, HttpMethod.Get, $"/api/v1/admin/banks/{bankB}/users")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(scopedToA.Sub, HttpMethod.Post, $"/api/v1/admin/banks/{bankB}/users", Invite($"b-{Guid.NewGuid():N}@example.test", "B", role))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(scopedToA.Sub, HttpMethod.Patch, $"/api/v1/admin/banks/{bankB}/users/{userB.Id}", """{"fullName":"x"}""")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(scopedToA.Sub, HttpMethod.Delete, $"/api/v1/admin/banks/{bankB}/users/{userB.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetOk<PagedResult<BankDto>>(scopedToA.Sub, "/api/v1/admin/banks?pageSize=100")).Items.Select(b => b.Id).Should().Equal(bankA);

        // Bank B's user addressed through bank A's path is not bank A's user.
        (await Send(scopedToA.Sub, HttpMethod.Patch, $"/api/v1/admin/banks/{bankA}/users/{userB.Id}", """{"fullName":"x"}""")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(scopedToA.Sub, HttpMethod.Delete, $"/api/v1/admin/banks/{bankA}/users/{userB.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Null_bank_id_never_grants_cross_bank_access()
    {
        var bank = await fixture.Data.CreateBankAsync();
        await fixture.Data.CreateUserWithPermissionsAsync("BANK", bank);

        // A processor row has bank_id NULL by construction; with no user_bank_scopes rows it sees no bank at all.
        var unscoped = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewBankUsers, Permissions.ManageBankUsers);
        (await fixture.Data.QueryScalarAsync<long>("SELECT count(*) FROM chargeback_diagram.users WHERE id = @id AND bank_id IS NULL", new { id = unscoped.Id })).Should().Be(1);

        (await Send(unscoped.Sub, HttpMethod.Get, $"/api/v1/admin/banks/{bank}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(unscoped.Sub, HttpMethod.Get, $"/api/v1/admin/banks/{bank}/users")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetOk<PagedResult<BankDto>>(unscoped.Sub, "/api/v1/admin/banks")).TotalCount.Should().Be(0);

        // An expired scope row does not count either.
        await fixture.Data.GrantScopeAsync(unscoped.Id, bank, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
        (await Send(unscoped.Sub, HttpMethod.Get, $"/api/v1/admin/banks/{bank}/users")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Permissions_and_user_type_are_enforced()
    {
        var bank = await fixture.Data.CreateBankAsync();
        var member = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bank);
        var viewOnly = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewBankUsers);
        var nothing = await fixture.Data.CreateUserWithPermissionsAsync("PROCESSOR", null, Permissions.ViewCases);
        await fixture.Data.GrantScopeAsync(viewOnly.Id, bank);
        await fixture.Data.GrantScopeAsync(nothing.Id, bank);
        var role = await RoleId("Bank User");

        (await Send(viewOnly.Sub, HttpMethod.Post, $"/api/v1/admin/banks/{bank}/users", Invite($"v-{Guid.NewGuid():N}@example.test", "V", role))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(viewOnly.Sub, HttpMethod.Patch, $"/api/v1/admin/banks/{bank}/users/{member.Id}", """{"fullName":"x"}""")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(viewOnly.Sub, HttpMethod.Delete, $"/api/v1/admin/banks/{bank}/users/{member.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(nothing.Sub, HttpMethod.Get, "/api/v1/admin/banks")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(nothing.Sub, HttpMethod.Get, $"/api/v1/admin/banks/{bank}/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Bank_detail_counts_active_users_by_role_and_lists_caller_permissions()
    {
        var bank = await fixture.Data.CreateBankAsync();
        var manager = await Manager("PROCESSOR", bank);
        var role = await RoleId("Bank User");
        var active = await fixture.Data.CreateUserWithPermissionsAsync("BANK", bank);
        await fixture.Data.ExecuteAsync("UPDATE chargeback_diagram.users SET role_id = @role WHERE id = @id", new { role, id = active.Id });
        (await Send(manager.Sub, HttpMethod.Post, $"/api/v1/admin/banks/{bank}/users", Invite($"i-{Guid.NewGuid():N}@example.test", "Invited", role))).StatusCode
            .Should().Be(HttpStatusCode.Created);

        var detail = await GetOk<BankDetailDto>(manager.Sub, $"/api/v1/admin/banks/{bank}");

        detail.ActiveUserCount.Should().Be(1, "the invited user is DISABLED");
        detail.UsersByRole.Should().ContainSingle().Which.Should().Be(new RoleUserCountDto(role, "Bank User", 1));
        detail.CallerPermissions.Should().BeEquivalentTo([Permissions.ViewBankUsers, Permissions.ManageBankUsers]);
    }

    [Fact]
    public async Task Any_platform_user_can_list_roles_matching_the_seeded_matrix()
    {
        var bankUser = await fixture.Data.CreateUserWithPermissionsAsync("BANK", await fixture.Data.CreateBankAsync());

        var roles = await GetOk<List<RoleDto>>(bankUser.Sub, "/api/v1/admin/roles");

        string[] common = [Permissions.ViewCases, Permissions.UpdateCaseStatus, Permissions.ViewTriage, Permissions.ViewBankUsers,
            Permissions.ReviewCase, Permissions.UploadDocument, Permissions.ViewDocuments, Permissions.ManageBankUsers];
        string[] senior = [.. common, Permissions.AssignCase, Permissions.RetriageCase];
        var expected = new Dictionary<string, (UserType Type, string[] Permissions)>
        {
            ["Analyst"] = (UserType.Processor, common),
            ["Senior Analyst"] = (UserType.Processor, senior),
            ["Compliance Officer"] = (UserType.Processor, common),
            ["Admin"] = (UserType.Admin, senior),
            ["Bank User"] = (UserType.Bank, []),
        };
        foreach (var (name, (type, permissions)) in expected)
        {
            var role = roles.Should().ContainSingle(r => r.Name == name).Subject;
            role.RoleType.Should().Be(type, name);
            role.Permissions.Should().BeEquivalentTo(permissions, name);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private async Task<TestUser> Manager(string userType, Guid bank)
    {
        var manager = await fixture.Data.CreateUserWithPermissionsAsync(userType, null, Permissions.ViewBankUsers, Permissions.ManageBankUsers);
        await fixture.Data.GrantScopeAsync(manager.Id, bank);
        return manager;
    }

    private Task<Guid> RoleId(string name) =>
        fixture.Data.QueryScalarAsync<Guid>("SELECT id FROM chargeback_diagram.roles WHERE name = @name", new { name });

    private Task<long> Count(string sql, string token) => fixture.Data.QueryScalarAsync<long>(sql, new { token });

    private static string Invite(string email, string fullName, Guid roleId) => JsonSerializer.Serialize(new { email, fullName, roleId });

    private async Task<List<(string EventType, JsonElement Data)>> AuditTrail(Guid userId)
    {
        var json = await fixture.Data.QueryScalarAsync<string>(
            """
            SELECT coalesce(json_agg(json_build_object('type', event_type, 'data', event_data->'data') ORDER BY created_at, id), '[]')::text
            FROM chargeback_diagram.domain_events
            WHERE event_data->'data'->>'userId' = @userId
            """,
            new { userId = userId.ToString() });
        return JsonDocument.Parse(json).RootElement.EnumerateArray()
            .Select(e => (e.GetProperty("type").GetString()!, e.GetProperty("data").Clone()))
            .ToList();
    }

    private async Task<HttpResponseMessage> Send(string sub, HttpMethod method, string url, string? body = null)
    {
        using var client = fixture.Factory.CreateClientFor(sub);
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    private async Task<T> GetOk<T>(string sub, string url)
    {
        var response = await Send(sub, HttpMethod.Get, url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(CurrentUserTests.Json))!;
    }
}
