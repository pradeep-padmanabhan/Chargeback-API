using System.Text.Json;
using Chargeback.SharedKernel.Security;

namespace Chargeback.Api.Features.Admin.Contracts;

// Fields mirror baseline columns (banks, users, roles, permissions, user_bank_scopes,
// scheme_reason_codes, scheme_rule_specs). No invented fields.

public sealed record BankDto(Guid Id, string BankCode, string BankName, string? Country, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CreateBankRequest(string BankCode, string BankName, string? Country);

public sealed record BankUserDto(Guid Id, Guid BankId, string Email, string FullName, UserType UserType, Guid RoleId, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Cognito identity provisioning for new users is an open question (Q-new-3).</summary>
public sealed record CreateBankUserRequest(string Email, string FullName, Guid RoleId);

public sealed record UpdateBankUserRequest(string? FullName, Guid? RoleId);

public sealed record PermissionDto(Guid Id, string Name, string Resource, string Action, string? Description, bool IsActive);

public sealed record RoleDto(Guid Id, string Name, string? Description, UserType RoleType, bool IsActive, IReadOnlyList<string> Permissions);

public sealed record BankScopeDto(Guid BankId, Guid? GrantedBy, DateTimeOffset ValidFrom, DateTimeOffset? ValidUntil);

public sealed record BankScopeGrant(Guid BankId, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil);

public sealed record ReplaceBankScopesRequest(IReadOnlyList<BankScopeGrant> Scopes);

public sealed record ReasonCodeDto(Guid Id, string Code, string Description, string? Category, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

public sealed record SchemeRuleSpecDto(
    Guid Id,
    Guid ReasonCodeId,
    string Scenario,
    JsonElement ConditionsJson,
    JsonElement RequiredDocs,
    int? TimeLimitDays,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string ApprovalStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Creates a DRAFT rule version. Rule values are supplied by approved business owners, never by the platform.</summary>
public sealed record CreateSchemeRuleSpecRequest(
    Guid ReasonCodeId,
    string Scenario,
    JsonElement ConditionsJson,
    JsonElement RequiredDocs,
    int? TimeLimitDays,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo);
