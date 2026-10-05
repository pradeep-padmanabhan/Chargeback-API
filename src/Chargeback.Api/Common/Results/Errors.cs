using Chargeback.SharedKernel.Results;

namespace Chargeback.Api.Common.Results;

/// <summary>Cross-cutting error codes. The codes are part of the public API contract.</summary>
public static class Errors
{
    public static readonly Error Unauthenticated =
        Error.Unauthorized("UNAUTHENTICATED", "Authentication is required.");

    public static readonly Error UserNotProvisioned =
        Error.Forbidden("USER_NOT_PROVISIONED", "The authenticated identity is not a platform user.");

    public static readonly Error UserNotActive =
        Error.Forbidden("USER_NOT_ACTIVE", "The user account is not active.");

    public static readonly Error UserRoleMismatch =
        Error.Forbidden("USER_ROLE_MISMATCH", "The user's role does not match the user type.");

    public static readonly Error UserTypeNotAllowed =
        Error.Forbidden("USER_TYPE_NOT_ALLOWED", "This operation is not available to this type of user.");

    public static readonly Error PermissionDenied =
        Error.Forbidden("PERMISSION_DENIED", "The user lacks the permission required for this operation.");

    public static readonly Error SystemOperationOnly =
        Error.Forbidden("SYSTEM_OPERATION_ONLY", "This operation can only be performed by the platform workflow.");

    public static readonly Error AuthorizationMisconfigured =
        Error.Forbidden("AUTHORIZATION_NOT_DECLARED", "This operation has no valid authorization declaration.");

    /// <summary>
    /// Returned both when a resource does not exist and when it belongs to a bank outside the caller's
    /// scope, so existence is never disclosed across banks.
    /// </summary>
    public static readonly Error ResourceNotFound =
        Error.NotFound("RESOURCE_NOT_FOUND", "The requested resource was not found.");

    public static readonly Error IdempotencyKeyRequired =
        Error.Failure("IDEMPOTENCY_KEY_REQUIRED", "A valid Idempotency-Key header is required for this operation.");

    public static Error InvalidSortField(string field, IReadOnlyList<string> supported) =>
        Error.Failure("INVALID_SORT_FIELD", $"Cannot sort by '{field}'. Supported: {string.Join(", ", supported)}.");

    public static Error NotImplemented(string operation) =>
        Error.NotImplemented("NOT_IMPLEMENTED", $"'{operation}' is defined by the API contract but not implemented yet.");
}
