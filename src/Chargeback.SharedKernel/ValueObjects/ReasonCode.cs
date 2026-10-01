using Chargeback.SharedKernel.Results;

namespace Chargeback.SharedKernel.ValueObjects;

/// <summary>
/// Format-only wrapper for a scheme reason code (<c>scheme_reason_codes.code</c>, varchar(16)).
/// It carries no scheme meaning: which code applies is decided only by the approved,
/// versioned scheme rules in the Triage &amp; Rules slice — never by AI.
/// </summary>
public sealed record ReasonCode
{
    public const int MaxLength = 16;

    private ReasonCode(string value) => Value = value;

    public string Value { get; }

    public static Result<ReasonCode> Create(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized)
            || normalized.Length > MaxLength
            || !normalized.All(c => char.IsAsciiLetterOrDigit(c) || c == '.'))
        {
            return Error.Failure("REASON_CODE_INVALID", "Reason code must be 1-16 letters, digits or dots.");
        }

        return new ReasonCode(normalized);
    }

    public override string ToString() => Value;
}
