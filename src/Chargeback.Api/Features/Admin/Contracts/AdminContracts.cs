using System.Text.Json;
using Chargeback.SharedKernel.Security;

namespace Chargeback.Api.Features.Admin.Contracts;

// Fields mirror baseline columns (banks, users, roles, permissions, user_bank_scopes,
// scheme_reason_codes, scheme_rule_specs). No invented fields.

public sealed record BankDto(Guid Id, string BankCode, string BankName, string? Country, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CreateBankRequest(string BankCode, string BankName, string? Country);

/// <summary>
/// A bank's user. <c>InvitedAt</c> is set for invited users, which stay DISABLED until Cognito identity linking exists.
/// <c>DeletedAt</c> is set for removed users (kept for audit; always DISABLED).
/// </summary>
public sealed record BankUserDto(
    Guid Id,
    Guid BankId,
    string Email,
    string FullName,
    UserType UserType,
    Guid RoleId,
    string Status,
    DateTimeOffset? InvitedAt,
    DateTimeOffset? DeletedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Active users of a bank holding one role.</summary>
public sealed record RoleUserCountDto(Guid RoleId, string RoleName, int ActiveUsers);

/// <summary>
/// One bank with its active-user count, the active users per role, and the permissions the caller holds (what the caller
/// may do with this bank in the admin pages; the bank is already within the caller's scope).
/// </summary>
public sealed record BankDetailDto(
    Guid Id,
    string BankCode,
    string BankName,
    string? Country,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int ActiveUserCount,
    IReadOnlyList<RoleUserCountDto> UsersByRole,
    IReadOnlyList<string> CallerPermissions);

/// <summary>Invite a bank user. <c>RoleId</c> must be an active BANK-type role (e.g. "Bank User").</summary>
public sealed record CreateBankUserRequest(string? Email, string? FullName, Guid? RoleId);

/// <summary>
/// The invited user plus a one-time invite token. KNOWN_LIMITATION_INVITE_EMAIL_: no email is sent and the token is a
/// placeholder that is not stored and cannot be redeemed; Cognito provisioning is an open decision.
/// </summary>
public sealed record InvitedBankUserDto(BankUserDto User, string InviteToken, string InviteDelivery);

/// <summary>Change name, role (audited as user.role.changed) or active state. At least one field.</summary>
public sealed record UpdateBankUserRequest(string? FullName, Guid? RoleId, bool? IsActive);

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
