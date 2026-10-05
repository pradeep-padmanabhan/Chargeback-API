using Chargeback.SharedKernel.Entities;
using Chargeback.SharedKernel.Security;

namespace Chargeback.Infrastructure.Persistence.Entities;

// Persistence mappings for the baseline schema (chargeback_diagram). One class per table,
// property-for-column. No table or column exists here that is not in
// docs/CHARGEBACK_DIAGRAM_BASELINE.sql; SchemaConformanceTests enforce this.

/// <summary><c>banks</c></summary>
public sealed class Bank : AuditableEntity
{
    public required string BankCode { get; set; }

    public required string BankName { get; set; }

    public string? Country { get; set; }

    public string Status { get; set; } = "ACTIVE";
}

/// <summary><c>roles</c></summary>
public sealed class Role : AuditableEntity
{
    public required string Name { get; set; }

    public string? Description { get; set; }

    public UserType RoleType { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary><c>permissions</c></summary>
public sealed class Permission : AuditableEntity
{
    public required string Name { get; set; }

    public required string Resource { get; set; }

    public required string Action { get; set; }

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary><c>role_permissions</c></summary>
public sealed class RolePermission : BaseEntity, IHasCreatedAt
{
    public Guid RoleId { get; set; }

    public Guid PermissionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary><c>users</c></summary>
public sealed class User : AuditableEntity
{
    public Guid? BankId { get; set; }

    public required string CognitoSub { get; set; }

    public required string Email { get; set; }

    public required string FullName { get; set; }

    public UserType UserType { get; set; }

    public Guid RoleId { get; set; }

    public string Status { get; set; } = "ACTIVE";

    /// <summary>Migration 0008: set for invited users (placeholder <c>cognito_sub</c>, DISABLED until identity linking exists).</summary>
    public DateTimeOffset? InvitedAt { get; set; }

    /// <summary>Migration 0008: soft delete; a deleted user is always DISABLED.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}

/// <summary><c>user_bank_scopes</c> (composite key user_id, bank_id)</summary>
public sealed class UserBankScope : IHasCreatedAt
{
    public Guid UserId { get; set; }

    public Guid BankId { get; set; }

    public Guid? GrantedBy { get; set; }

    public DateTimeOffset ValidFrom { get; set; }

    public DateTimeOffset? ValidUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
