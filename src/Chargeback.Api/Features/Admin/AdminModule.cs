using Carter;
using Chargeback.Api.Common.Endpoints;
using Chargeback.Api.Common.Paging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Features.Admin.Contracts;
using Chargeback.SharedKernel.Paging;
using MediatR;

namespace Chargeback.Api.Features.Admin;

public sealed class AdminModule : ICarterModule
{
    private const string StubPhase = "Phase 12 (Admin & Configuration)";

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup($"{EndpointConventions.ApiPrefix}/admin").WithTags("Admin");

        admin.MapGet("/banks", (string? status, int? page, int? pageSize, string? sortBy, string? sortDirection, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ListBanksQuery(status, new PageRequest(page, pageSize, sortBy, sortDirection)), http))
            .WithContract<PagedResult<BankDto>>("listBanks", "Banks within the caller's bank scope (VIEW_BANK_USERS; processor/admin)")
            .WithSortFields(ListBanksQuery.Sorts);

        admin.MapGet("/banks/{bankId:guid}", (Guid bankId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetBankQuery(bankId), http))
            .WithContract<BankDetailDto>("getBank", "One bank with active-user counts per role and the caller's permissions (404 if outside scope)");

        admin.MapPost("/banks", (CreateBankRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new CreateBankCommand(body), http, dto => TypedResults.Created($"/api/v1/admin/banks/{dto.Id}", dto)))
            .WithContract<BankDto>("createBank", "Create a bank tenant", StatusCodes.Status201Created)
            .AsStub(StubPhase);

        admin.MapGet("/banks/{bankId:guid}/users", (Guid bankId, int? page, int? pageSize, string? sortBy, string? sortDirection, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ListBankUsersQuery(bankId, new PageRequest(page, pageSize, sortBy, sortDirection)), http))
            .WithContract<PagedResult<BankUserDto>>("listBankUsers", "Users of a bank (VIEW_BANK_USERS + bank scope)")
            .WithSortFields(ListBankUsersQuery.Sorts);

        admin.MapPost("/banks/{bankId:guid}/users", (Guid bankId, CreateBankUserRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new CreateBankUserCommand(bankId, body), http,
                    dto => TypedResults.Created($"/api/v1/admin/banks/{bankId}/users/{dto.User.Id}", dto)))
            .WithContract<InvitedBankUserDto>("createBankUser", "Invite a bank user (MANAGE_BANK_USERS)", StatusCodes.Status201Created)
            .WithDescription(
                "Body {email, fullName, roleId}; roleId must be an active BANK-type role (422 ROLE_NOT_ASSIGNABLE); 409 USER_EMAIL_IN_USE. " +
                "The user is DISABLED with a placeholder identity until Cognito linking exists. KNOWN_LIMITATION_INVITE_EMAIL_: no email is sent; " +
                "the returned inviteToken is a placeholder, not stored and not redeemable.")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        admin.MapPatch("/banks/{bankId:guid}/users/{userId:guid}", (Guid bankId, Guid userId, UpdateBankUserRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new UpdateBankUserCommand(bankId, userId, body), http))
            .WithContract<BankUserDto>("updateBankUser", "Update a bank user's name, role or active state (MANAGE_BANK_USERS)")
            .WithDescription(
                "Body {fullName?, roleId?, isActive?}, at least one. A role change is audited (user.role.changed). Deactivation applies from the " +
                "user's next request but does not revoke Cognito sessions (known limitation). 409 INVITE_PENDING when activating an invited user.")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        admin.MapDelete("/banks/{bankId:guid}/users/{userId:guid}", (Guid bankId, Guid userId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new DeleteBankUserCommand(bankId, userId), http, _ => TypedResults.NoContent()))
            .WithContract<BankUserDto>("deleteBankUser", "Remove a bank user: soft delete, audited (MANAGE_BANK_USERS)", StatusCodes.Status204NoContent)
            .WithDescription("The row is kept with deletedAt and status DISABLED (user.deleted). 409 CANNOT_DELETE_SELF.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        admin.MapGet("/users/{userId:guid}/bank-scopes", (Guid userId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetUserBankScopesQuery(userId), http))
            .WithContract<IReadOnlyList<BankScopeDto>>("getUserBankScopes", "Explicit bank scopes of a processor/admin user")
            .AsStub(StubPhase);

        admin.MapPut("/users/{userId:guid}/bank-scopes", (Guid userId, ReplaceBankScopesRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ReplaceUserBankScopesCommand(userId, body), http))
            .WithContract<IReadOnlyList<BankScopeDto>>("replaceUserBankScopes", "Replace a processor/admin user's bank scopes")
            .AsStub(StubPhase);

        admin.MapGet("/roles", (ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ListRolesQuery(), http))
            .WithContract<IReadOnlyList<RoleDto>>("listRoles", "Roles and their permissions (any platform user; read-only)");

        admin.MapGet("/permissions", (ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ListPermissionsQuery(), http))
            .WithContract<IReadOnlyList<PermissionDto>>("listPermissions", "Permission catalogue");

        admin.MapGet("/reason-codes", (ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ListReasonCodesQuery(), http))
            .WithContract<IReadOnlyList<ReasonCodeDto>>("listReasonCodes", "Scheme reason codes (versioned)")
            .AsStub(StubPhase);

        admin.MapGet("/scheme-rules", (ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ListSchemeRulesQuery(), http))
            .WithContract<IReadOnlyList<SchemeRuleSpecDto>>("listSchemeRules", "Versioned scheme rule specs")
            .AsStub(StubPhase);

        admin.MapPost("/scheme-rules", (CreateSchemeRuleSpecRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new CreateSchemeRuleCommand(body), http, dto => TypedResults.Created($"/api/v1/admin/scheme-rules/{dto.Id}", dto)))
            .WithContract<SchemeRuleSpecDto>("createSchemeRule", "Create a DRAFT scheme rule version", StatusCodes.Status201Created)
            .AsStub(StubPhase);

        admin.MapPost("/scheme-rules/{ruleId:guid}/approval", (Guid ruleId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ApproveSchemeRuleCommand(ruleId), http))
            .WithContract<SchemeRuleSpecDto>("approveSchemeRule", "Approve a DRAFT scheme rule version")
            .AsStub(StubPhase);
    }
}
