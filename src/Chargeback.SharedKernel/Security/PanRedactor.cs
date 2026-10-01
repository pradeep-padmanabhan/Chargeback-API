using System.Text;
using System.Text.RegularExpressions;

namespace Chargeback.SharedKernel.Security;

/// <summary>
/// Detects and masks primary account numbers in free text.
/// A <b>candidate</b> is any 13–19 digit run: contiguous, or grouped 4-4-4-x / 4-6-5 with a consistent
/// space or dash separator.
/// <list type="bullet">
/// <item><see cref="ContainsPan"/> — candidate <b>and</b> Luhn-valid. Used to <b>reject</b> input, so long
/// store references or loyalty numbers that fail Luhn are accepted.</item>
/// <item><see cref="ContainsPanCandidate"/> / <see cref="Redact"/> — every candidate, Luhn or not. Used for
/// <b>masking</b> logs and AI input, where over-masking is harmless and under-masking is not.</item>
/// </list>
/// Neither can detect CVV/CVC values; those must never be accepted into free text at all.
/// </summary>
public static partial class PanRedactor
{
    /// <summary>True when the text contains a digit run that is shaped like a PAN and passes the Luhn check.</summary>
    public static bool ContainsPan(string? input) =>
        !string.IsNullOrEmpty(input) && PanPattern().Matches(input).Any(m => PassesLuhn(Digits(m.Value)));

    /// <summary>True when the text contains any PAN-shaped digit run, whether or not it passes Luhn.</summary>
    public static bool ContainsPanCandidate(string? input) =>
        !string.IsNullOrEmpty(input) && PanPattern().IsMatch(input);

    /// <summary>Masks every PAN-shaped digit run to its last four digits (conservative: Luhn is not required).</summary>
    public static string Redact(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return PanPattern().Replace(input, match => Mask(match.Value));
    }

    /// <summary>Standard Luhn (mod 10) check over a string of ASCII digits.</summary>
    public static bool PassesLuhn(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);
        if (digits.Length == 0)
        {
            return false;
        }

        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var c = digits[i];
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            var n = c - '0';
            if (doubleIt)
            {
                n *= 2;
                if (n > 9)
                {
                    n -= 9;
                }
            }

            sum += n;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }

    private static string Digits(string candidate)
    {
        var digits = new StringBuilder(candidate.Length);
        foreach (var c in candidate)
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
        }

        return digits.ToString();
    }

    private static string Mask(string candidate)
    {
        var digits = Digits(candidate);
        return new string('*', digits.Length - 4) + digits[^4..];
    }

    [GeneratedRegex(
        @"(?<!\d)(?:\d{13,19}|\d{4}([ -])\d{4}\1\d{4}\1\d{1,7}|\d{4}([ -])\d{6}\2\d{5})(?!\d)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex PanPattern();
}
