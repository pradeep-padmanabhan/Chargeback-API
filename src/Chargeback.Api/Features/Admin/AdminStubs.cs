using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Admin.Contracts;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;

namespace Chargeback.Api.Features.Admin;

// Phase 4 contract stubs (Phase 12: Admin & Configuration).

[RequirePermission(Permissions.ManageBanks)]
[RestrictToUserTypes(UserType.Admin)]
[NotBankScoped("Creates a new bank tenant; no existing bank scope applies.")]
public sealed record CreateBankCommand(CreateBankRequest Body) : ICommand<BankDto>, ITransactionalCommand;

[RequirePermission(Permissions.CreateBankUser)]
public sealed record CreateBankUserCommand(Guid BankId, CreateBankUserRequest Body) : ICommand<BankUserDto>, IBankScopedRequest, ITransactionalCommand;

[RequirePermission(Permissions.UpdateBankUser)]
public sealed record UpdateBankUserCommand(Guid UserId, UpdateBankUserRequest Body) : ICommand<BankUserDto>, IResourceScopedRequest, ITransactionalCommand
{
    public ScopedResource Resource => new(ScopedResourceKind.BankUser, UserId);
}

[RequirePermission(Permissions.DisableBankUser)]
public sealed record DisableBankUserCommand(Guid UserId) : ICommand, IResourceScopedRequest, ITransactionalCommand
{
    public ScopedResource Resource => new(ScopedResourceKind.BankUser, UserId);
}

[RequirePermission(Permissions.ManageRoles)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
[NotBankScoped("Roles are global configuration.")]
public sealed record ListRolesQuery : IQuery<IReadOnlyList<RoleDto>>;

/// <summary>Handler must also verify the granter holds scope for every bank granted (Phase 12).</summary>
[RequirePermission(Permissions.ManageBankScopes)]
[RestrictToUserTypes(UserType.Admin)]
[NotBankScoped("Targets processor/admin users, who are not bank-owned; handler checks granter scope per bank.")]
public sealed record GetUserBankScopesQuery(Guid UserId) : IQuery<IReadOnlyList<BankScopeDto>>;

[RequirePermission(Permissions.ManageBankScopes)]
[RestrictToUserTypes(UserType.Admin)]
[NotBankScoped("Targets processor/admin users, who are not bank-owned; handler checks granter scope per bank.")]
public sealed record ReplaceUserBankScopesCommand(Guid UserId, ReplaceBankScopesRequest Body)
    : ICommand<IReadOnlyList<BankScopeDto>>, ITransactionalCommand;

[RequirePermission(Permissions.ViewSchemeRules)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
[NotBankScoped("Scheme reason codes are global scheme configuration.")]
public sealed record ListReasonCodesQuery : IQuery<IReadOnlyList<ReasonCodeDto>>;

[RequirePermission(Permissions.ViewSchemeRules)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
[NotBankScoped("Scheme rules are global scheme configuration.")]
public sealed record ListSchemeRulesQuery : IQuery<IReadOnlyList<SchemeRuleSpecDto>>;

[RequirePermission(Permissions.ManageSchemeRules)]
[RestrictToUserTypes(UserType.Admin)]
[NotBankScoped("Scheme rules are global scheme configuration.")]
public sealed record CreateSchemeRuleCommand(CreateSchemeRuleSpecRequest Body) : ICommand<SchemeRuleSpecDto>, ITransactionalCommand;

/// <summary>DRAFT → APPROVED. Approval workflow (maker/checker) is an open question.</summary>
[RequirePermission(Permissions.ManageSchemeRules)]
[RestrictToUserTypes(UserType.Admin)]
[NotBankScoped("Scheme rules are global scheme configuration.")]
public sealed record ApproveSchemeRuleCommand(Guid RuleId) : ICommand<SchemeRuleSpecDto>, ITransactionalCommand;

internal sealed class CreateBankHandler : NotImplementedHandler<CreateBankCommand, Result<BankDto>>;

internal sealed class CreateBankUserHandler : NotImplementedHandler<CreateBankUserCommand, Result<BankUserDto>>;

internal sealed class UpdateBankUserHandler : NotImplementedHandler<UpdateBankUserCommand, Result<BankUserDto>>;

internal sealed class DisableBankUserHandler : NotImplementedHandler<DisableBankUserCommand, Result>;

internal sealed class ListRolesHandler : NotImplementedHandler<ListRolesQuery, Result<IReadOnlyList<RoleDto>>>;

internal sealed class GetUserBankScopesHandler : NotImplementedHandler<GetUserBankScopesQuery, Result<IReadOnlyList<BankScopeDto>>>;

internal sealed class ReplaceUserBankScopesHandler : NotImplementedHandler<ReplaceUserBankScopesCommand, Result<IReadOnlyList<BankScopeDto>>>;

internal sealed class ListReasonCodesHandler : NotImplementedHandler<ListReasonCodesQuery, Result<IReadOnlyList<ReasonCodeDto>>>;

internal sealed class ListSchemeRulesHandler : NotImplementedHandler<ListSchemeRulesQuery, Result<IReadOnlyList<SchemeRuleSpecDto>>>;

internal sealed class CreateSchemeRuleHandler : NotImplementedHandler<CreateSchemeRuleCommand, Result<SchemeRuleSpecDto>>;

internal sealed class ApproveSchemeRuleHandler : NotImplementedHandler<ApproveSchemeRuleCommand, Result<SchemeRuleSpecDto>>;
