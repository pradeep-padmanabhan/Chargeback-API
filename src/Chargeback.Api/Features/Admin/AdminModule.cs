using Carter;
using Chargeback.Api.Common.Endpoints;
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

        admin.MapGet("/banks", (int? page, int? pageSize, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ListBanksQuery(new PageRequest(page, pageSize)), http))
            .WithContract<PagedResult<BankDto>>("listBanks", "Banks within the caller's bank scope");

        admin.MapGet("/banks/{bankId:guid}", (Guid bankId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetBankQuery(bankId), http))
            .WithContract<BankDto>("getBank", "One bank (404 if outside scope)");

        admin.MapPost("/banks", (CreateBankRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new CreateBankCommand(body), http, dto => TypedResults.Created($"/api/v1/admin/banks/{dto.Id}", dto)))
            .WithContract<BankDto>("createBank", "Create a bank tenant", StatusCodes.Status201Created)
            .AsStub(StubPhase);

        admin.MapGet("/banks/{bankId:guid}/users", (Guid bankId, int? page, int? pageSize, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new ListBankUsersQuery(bankId, new PageRequest(page, pageSize)), http))
            .WithContract<PagedResult<BankUserDto>>("listBankUsers", "Users of a bank (VIEW_BANK_USERS + bank scope)");

        admin.MapPost("/banks/{bankId:guid}/users", (Guid bankId, CreateBankUserRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new CreateBankUserCommand(bankId, body), http, dto => TypedResults.Created($"/api/v1/admin/users/{dto.Id}", dto)))
            .WithContract<BankUserDto>("createBankUser", "Create a bank user", StatusCodes.Status201Created)
            .AsStub(StubPhase);

        admin.MapPatch("/users/{userId:guid}", (Guid userId, UpdateBankUserRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new UpdateBankUserCommand(userId, body), http))
            .WithContract<BankUserDto>("updateBankUser", "Update a bank user")
            .AsStub(StubPhase);

        admin.MapPost("/users/{userId:guid}/disable", (Guid userId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new DisableBankUserCommand(userId), http))
            .WithNoContentContract("disableBankUser", "Disable a bank user")
            .AsStub(StubPhase);

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
            .WithContract<IReadOnlyList<RoleDto>>("listRoles", "Roles and their permissions")
            .AsStub(StubPhase);

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
