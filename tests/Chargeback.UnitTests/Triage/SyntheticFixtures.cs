using Chargeback.Api.Features.Triage.Conditions;
using Chargeback.Api.Features.Triage.IssuerTriage;
using Chargeback.Api.Features.Triage.SchemeRules;
using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.UnitTests.Triage;

/// <summary>
/// SYNTHETIC TEST FIXTURES. Reason codes (SYN-*), rule conditions, time limits, weights and thresholds below
/// are invented solely to exercise engine mechanics. They are NOT scheme rules or bank policy, and passing
/// tests prove nothing about real business rules.
/// </summary>
internal static class Synthetic
{
    public static readonly DateOnly Day = new(2026, 6, 15);

    public static SchemeRuleSpecRecord Rule(
        string code = "SYN-001",
        string conditions = """{"fact":"dispute.currencyCode","op":"eq","value":"GBP"}""",
        string docs = """[{"slotName":"SYN letter","required":true,"expectedType":"SYN-LETTER"}]""",
        int? timeLimitDays = 30,
        string status = "APPROVED",
        DateOnly? from = null,
        DateOnly? to = null,
        DateOnly? codeFrom = null,
        DateOnly? codeTo = null,
        Guid? id = null) =>
        new(id ?? Guid.NewGuid(), Guid.NewGuid(), "SYNTHETIC scenario " + code, conditions, docs, timeLimitDays,
            from ?? new DateOnly(2026, 1, 1), to, status, code, "SYNTHETIC reason code", "SYNTHETIC",
            codeFrom ?? new DateOnly(2026, 1, 1), codeTo);

    public static CaseFacts Facts(string currency = "GBP", decimal amount = 100m, string merchant = "SYN merchant") =>
        CaseFacts.Empty
            .With(FactCatalog.CurrencyCode, FactValue.Of(currency))
            .With(FactCatalog.TransactionAmount, FactValue.Of(amount))
            .With(FactCatalog.MerchantName, FactValue.Of(merchant))
            .With(FactCatalog.IntakeChannel, FactValue.Of("PORTAL"));

    public static SchemeRulesOptions FullOptions() => new()
    {
        CalendarTimeZone = "UTC",
        EffectiveDateBasis = SchemeDateBasis.TransactionDate,
        ClockStartBasis = SchemeDateBasis.TransactionDate,
        DeadlineDayCounting = DeadlineDayCounting.CalendarDays,
    };

    public static SchemeEvaluationInput Input(CaseFacts? facts = null, DateOnly? transactionDay = null) =>
        new(facts ?? Facts(), new DateTimeOffset((transactionDay ?? Day).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero), new DateTimeOffset(2026, 6, 20, 9, 0, 0, TimeSpan.Zero));

    public static SchemeRuleEvaluation DeterminedScheme(string code = "SYN-001") =>
        new(SchemeEvaluationStatus.Determined, "synthetic", Day, new DeterminedReasonCode(Guid.NewGuid(), code, "SYNTHETIC", null),
            Guid.NewGuid(), "SYNTHETIC", DeadlineResult.NotApplicable, [], [], null);

    /// <summary>Synthetic bank configuration with every component present; override parts per test.</summary>
    public static BankTriageConfiguration BankConfig(
        IReadOnlyList<HardEligibilityRule>? eligibility = null,
        RiskScoringModel? risk = null,
        IReadOnlyList<HumanReviewRule>? triggers = null,
        IReadOnlyList<RoutingRule>? routing = null) =>
        new("SYN-CONFIG-1",
            eligibility ?? [new("SYN-ELIG-CURRENCY", """{"fact":"dispute.currencyCode","op":"exists"}""", TriageOutcome.Invalid)],
            risk ?? new RiskScoringModel([new("SYN-RISK-HIGH-AMOUNT", """{"fact":"dispute.transactionAmount","op":"gte","value":5000}""", 60m)], 50m),
            triggers ?? [],
            routing ?? [new("SYN-ROUTE-DEFAULT", """{"fact":"dispute.intakeChannel","op":"eq","value":"PORTAL"}""", TriageOutcome.ProceedToFiling)]);
}

internal sealed class InMemoryRuleRepository(params SchemeRuleSpecRecord[] rules) : ISchemeRuleRepository
{
    public Exception? Failure { get; init; }

    public Task<IReadOnlyList<SchemeRuleSpecRecord>> GetAllAsync(CancellationToken cancellationToken) =>
        Failure is null ? Task.FromResult<IReadOnlyList<SchemeRuleSpecRecord>>(rules) : Task.FromException<IReadOnlyList<SchemeRuleSpecRecord>>(Failure);
}

internal sealed class FixedConfigProvider(BankTriageConfigurationResult result) : IBankTriageConfigurationProvider
{
    public Task<BankTriageConfigurationResult> GetApprovedAsync(Guid bankId, DateTimeOffset asOf, CancellationToken cancellationToken) =>
        Task.FromResult(result);
}
