using Chargeback.SharedKernel.Results;

namespace Chargeback.SharedKernel.ValueObjects;

/// <summary>
/// A masked card number that exposes the last four digits only, e.g. <c>************1234</c>.
/// It is impossible to construct one holding a full PAN: <see cref="Create"/> rejects any
/// input with visible digits outside the last four. Maps to <c>disputes.card_number_masked</c>.
/// </summary>
public sealed record CardMasked
{
    private const int MinLength = 12;
    private const int MaxLength = 19;
    private const char MaskChar = '*';

    private CardMasked(string value) => Value = value;

    public string Value { get; }

    public string Last4 => Value[^4..];

    /// <summary>Parses an already-masked card number. Accepts <c>*</c>, <c>X</c>, <c>x</c> or <c>•</c> as mask characters.</summary>
    public static Result<CardMasked> Create(string? masked)
    {
        if (string.IsNullOrWhiteSpace(masked))
        {
            return Invalid();
        }

        var compact = new string(masked.Where(c => c is not (' ' or '-')).ToArray());
        if (compact.Length is < MinLength or > MaxLength)
        {
            return Invalid();
        }

        var head = compact[..^4];
        var tail = compact[^4..];
        if (!tail.All(char.IsAsciiDigit) || !head.All(IsMaskChar))
        {
            return Invalid();
        }

        return new CardMasked(new string(MaskChar, head.Length) + tail);
    }

    /// <summary>
    /// Masks a full PAN received at an intake boundary. The PAN is not retained;
    /// callers must not persist or log the input.
    /// </summary>
    public static Result<CardMasked> FromPan(ReadOnlySpan<char> pan)
    {
        Span<char> digits = stackalloc char[MaxLength];
        var count = 0;
        foreach (var c in pan)
        {
            if (c is ' ' or '-')
            {
                continue;
            }

            if (!char.IsAsciiDigit(c) || count == MaxLength)
            {
                return Invalid();
            }

            digits[count++] = c;
        }

        if (count < MinLength)
        {
            return Invalid();
        }

        var result = new string(MaskChar, count - 4) + new string(digits[(count - 4)..count]);
        digits.Clear();
        return new CardMasked(result);
    }

    public override string ToString() => Value;

    private static bool IsMaskChar(char c) => c is MaskChar or 'X' or 'x' or '•';

    private static Error Invalid() =>
        Error.Failure("CARD_MASKED_INVALID", "Card number must be masked to the last four digits.");
}
