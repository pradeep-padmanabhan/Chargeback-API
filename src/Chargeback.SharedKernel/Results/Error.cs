namespace Chargeback.SharedKernel.Results;

public enum ErrorType
{
    Failure,
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    NotImplemented,

    /// <summary>A dependency (e.g. the database) is unavailable; nothing was decided or persisted. Safe to retry later.</summary>
    Unavailable,

    /// <summary>The caller's version precondition (If-Match) does not match the current resource version.</summary>
    PreconditionFailed,

    /// <summary>The operation requires a version precondition (If-Match) and none was supplied.</summary>
    PreconditionRequired,

    /// <summary>The request is well-formed but cannot be processed in the resource's current state (422).</summary>
    Unprocessable,
}

/// <summary>
/// A stable, machine-readable failure. <see cref="Code"/> is part of the public API contract
/// (UPPER_SNAKE_CASE); <see cref="Message"/> is safe to show to the caller and must never
/// contain card data or another bank's information.
/// </summary>
public sealed record Error(string Code, string Message, ErrorType Type)
{
    public IReadOnlyDictionary<string, string[]>? ValidationErrors { get; init; }

    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error NotImplemented(string code, string message) => new(code, message, ErrorType.NotImplemented);

    public static Error Unavailable(string code, string message) => new(code, message, ErrorType.Unavailable);

    public static Error PreconditionFailed(string code, string message) => new(code, message, ErrorType.PreconditionFailed);

    public static Error PreconditionRequired(string code, string message) => new(code, message, ErrorType.PreconditionRequired);

    public static Error Unprocessable(string code, string message) => new(code, message, ErrorType.Unprocessable);

    public static Error Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new("VALIDATION_FAILED", "One or more validation errors occurred.", ErrorType.Validation)
        {
            ValidationErrors = errors,
        };
}
