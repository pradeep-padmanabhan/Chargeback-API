using Chargeback.Infrastructure.Persistence.Entities;

namespace Chargeback.Api.Features.Triage.Conditions;

public enum FactType
{
    String,
    Number,
    Date,
    Boolean,
}

public sealed record FactDefinition(string Name, FactType Type, string Description);

/// <summary>
/// The closed set of structured facts that rules may reference (ADR-0120/ADR-0121). Only facts derivable
/// from approved baseline columns exist; a rule naming anything else is invalid rule data.
/// </summary>
public static class FactCatalog
{
    public const string IntakeChannel = "dispute.intakeChannel";
    public const string TransactionAmount = "dispute.transactionAmount";
    public const string CurrencyCode = "dispute.currencyCode";
    public const string TransactionDate = "dispute.transactionDate";
    public const string ReceivedDate = "dispute.receivedDate";
    public const string MerchantName = "dispute.merchantName";
    public const string HasAcquirerReferenceNumber = "dispute.hasAcquirerReferenceNumber";
    public const string HasCardNumber = "dispute.hasCardNumber";
    public const string SchemeReasonCode = "scheme.reasonCode";

    public static readonly IReadOnlyDictionary<string, FactDefinition> All = new[]
    {
        new FactDefinition(IntakeChannel, FactType.String, "disputes.intake_channel (PORTAL, EMAIL, BULK, API, SDK)"),
        new FactDefinition(TransactionAmount, FactType.Number, "disputes.transaction_amount"),
        new FactDefinition(CurrencyCode, FactType.String, "disputes.currency_code (upper case)"),
        new FactDefinition(TransactionDate, FactType.Date, "disputes.transaction_date as a date in the configured calendar time zone"),
        new FactDefinition(ReceivedDate, FactType.Date, "disputes.created_at as a date in the configured calendar time zone"),
        new FactDefinition(MerchantName, FactType.String, "disputes.merchant_name"),
        new FactDefinition(HasAcquirerReferenceNumber, FactType.Boolean, "disputes.acquirer_reference_number is present"),
        new FactDefinition(HasCardNumber, FactType.Boolean, "disputes.card_number_masked is present"),
        new FactDefinition(SchemeReasonCode, FactType.String, "reason code determined by the scheme rules layer (issuer layer only)"),
    }.ToDictionary(f => f.Name, StringComparer.Ordinal);
}

/// <summary>A typed fact value: string, decimal, DateOnly or bool.</summary>
public readonly record struct FactValue(FactType Type, object Value)
{
    public static FactValue Of(string value) => new(FactType.String, value);

    public static FactValue Of(decimal value) => new(FactType.Number, value);

    public static FactValue Of(DateOnly value) => new(FactType.Date, value);

    public static FactValue Of(bool value) => new(FactType.Boolean, value);

    public int CompareTo(FactValue other) => (Type, Value, other.Value) switch
    {
        (FactType.Number, decimal a, decimal b) => a.CompareTo(b),
        (FactType.Date, DateOnly a, DateOnly b) => a.CompareTo(b),
        (FactType.String, string a, string b) => string.CompareOrdinal(a, b),
        (FactType.Boolean, bool a, bool b) => a.CompareTo(b),
        _ => throw new InvalidOperationException("Fact values of different types cannot be compared."),
    };

    public bool EqualsValue(FactValue other) => Type == other.Type && CompareTo(other) == 0;
}

/// <summary>Immutable bag of known facts for one case. Absent facts are unknown (not false).</summary>
public sealed class CaseFacts
{
    private readonly Dictionary<string, FactValue> _values;

    private CaseFacts(Dictionary<string, FactValue> values) => _values = values;

    public static CaseFacts Empty { get; } = new(new Dictionary<string, FactValue>(StringComparer.Ordinal));

    public IReadOnlyDictionary<string, FactValue> Values => _values;

    public bool TryGet(string name, out FactValue value) => _values.TryGetValue(name, out value);

    public CaseFacts With(string name, FactValue value)
    {
        if (!FactCatalog.All.TryGetValue(name, out var definition) || definition.Type != value.Type)
        {
            throw new ArgumentException($"'{name}' is not a catalogued fact of type {value.Type}.", nameof(name));
        }

        return new CaseFacts(new Dictionary<string, FactValue>(_values, StringComparer.Ordinal) { [name] = value });
    }

    /// <summary>Facts from the dispute row. Date facts need the approved calendar time zone; without it they stay unknown.</summary>
    public static CaseFacts FromDispute(Dispute dispute, TimeZoneInfo? calendar)
    {
        ArgumentNullException.ThrowIfNull(dispute);

        var facts = Empty
            .With(FactCatalog.IntakeChannel, FactValue.Of(dispute.IntakeChannel))
            .With(FactCatalog.HasAcquirerReferenceNumber, FactValue.Of(!string.IsNullOrWhiteSpace(dispute.AcquirerReferenceNumber)))
            .With(FactCatalog.HasCardNumber, FactValue.Of(!string.IsNullOrWhiteSpace(dispute.CardNumberMasked)));

        if (dispute.TransactionAmount is { } amount)
        {
            facts = facts.With(FactCatalog.TransactionAmount, FactValue.Of(amount));
        }

        if (!string.IsNullOrWhiteSpace(dispute.CurrencyCode))
        {
            facts = facts.With(FactCatalog.CurrencyCode, FactValue.Of(dispute.CurrencyCode.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(dispute.MerchantName))
        {
            facts = facts.With(FactCatalog.MerchantName, FactValue.Of(dispute.MerchantName));
        }

        if (calendar is not null)
        {
            if (dispute.TransactionDate is { } transactionDate)
            {
                facts = facts.With(FactCatalog.TransactionDate, FactValue.Of(ToDate(transactionDate, calendar)));
            }

            facts = facts.With(FactCatalog.ReceivedDate, FactValue.Of(ToDate(dispute.CreatedAt, calendar)));
        }

        return facts;
    }

    public static DateOnly ToDate(DateTimeOffset instant, TimeZoneInfo calendar) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, calendar).DateTime);
}
