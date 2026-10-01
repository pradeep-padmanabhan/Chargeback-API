using Chargeback.SharedKernel.Results;

namespace Chargeback.SharedKernel.ValueObjects;

/// <summary>ISO 4217 alphabetic code format (three letters, upper case). Maps to <c>char(3)</c>.</summary>
public sealed record CurrencyCode
{
    private CurrencyCode(string value) => Value = value;

    public string Value { get; }

    public static Result<CurrencyCode> Create(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (normalized is null || normalized.Length != 3 || !normalized.All(char.IsAsciiLetterUpper))
        {
            return Error.Failure("CURRENCY_CODE_INVALID", "Currency code must be three letters (ISO 4217 format).");
        }

        return new CurrencyCode(normalized);
    }

    public override string ToString() => Value;
}
