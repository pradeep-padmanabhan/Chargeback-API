using Chargeback.SharedKernel.Results;

namespace Chargeback.SharedKernel.ValueObjects;

/// <summary>
/// An amount in a currency. Precision follows the baseline schema column
/// <c>numeric(18,2)</c>: at most 2 decimal places and 16 integer digits. Values are never
/// silently rounded. (Currencies with 3 minor units are an open question: ADR-0116.)
/// </summary>
public sealed record Money
{
    public const int MaxScale = 2;
    private const decimal MaxMagnitude = 9_999_999_999_999_999.99m;

    private Money(decimal amount, CurrencyCode currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public CurrencyCode Currency { get; }

    public static Result<Money> Create(decimal amount, CurrencyCode currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        if (decimal.Round(amount, MaxScale) != amount)
        {
            return Error.Failure("MONEY_SCALE_INVALID", $"Amount must have at most {MaxScale} decimal places.");
        }

        if (Math.Abs(amount) > MaxMagnitude)
        {
            return Error.Failure("MONEY_OUT_OF_RANGE", "Amount exceeds the supported range.");
        }

        return new Money(amount, currency);
    }

    public Result<Money> Add(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.Currency != Currency
            ? Error.Failure("MONEY_CURRENCY_MISMATCH", "Cannot combine amounts in different currencies.")
            : Create(Amount + other.Amount, Currency);
    }

    public override string ToString() => $"{Amount:0.00} {Currency}";
}
