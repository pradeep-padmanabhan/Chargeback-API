using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.Api.Features.Triage.IssuerTriage;

// In-memory shape of an APPROVED bank triage configuration (ADR-0119, proposed). There is deliberately no
// database storage: the baseline has no table for it and the design is not approved. Conditions use the
// same proposed JSON format as scheme rules (ADR-0120). All values are supplied by the bank/product owner.

/// <summary>A rule that must hold for the case to be eligible; when it does not, the configured outcome applies.</summary>
public sealed record HardEligibilityRule(string Id, string ConditionJson, TriageOutcome OutcomeWhenNotMet);

/// <summary>Adds <see cref="Weight"/> to the risk score when its condition is true.</summary>
public sealed record RiskFactor(string Id, string ConditionJson, decimal Weight);

public sealed record RiskScoringModel(IReadOnlyList<RiskFactor> Factors, decimal HumanReviewThreshold);

/// <summary>Sends the case to human review when its condition is true.</summary>
public sealed record HumanReviewRule(string Id, string ConditionJson, string Reason);

/// <summary>Ordered routing rule: the first rule whose condition is true decides the outcome.</summary>
public sealed record RoutingRule(string Id, string ConditionJson, TriageOutcome Outcome);

public sealed record BankTriageConfiguration(
    string Version,
    IReadOnlyList<HardEligibilityRule> HardEligibility,
    RiskScoringModel RiskScoring,
    IReadOnlyList<HumanReviewRule> HumanReviewTriggers,
    IReadOnlyList<RoutingRule> RoutingPolicy);

public enum BankTriageConfigurationStatus
{
    Approved,

    /// <summary>No approved configuration exists for the bank (today: always, pending ADR-0119).</summary>
    NotConfigured,

    /// <summary>The configuration store could not be read. Nothing may be decided or persisted.</summary>
    Unavailable,
}

public sealed record BankTriageConfigurationResult(BankTriageConfigurationStatus Status, BankTriageConfiguration? Configuration, string Detail)
{
    public static BankTriageConfigurationResult Approved(BankTriageConfiguration configuration) =>
        new(BankTriageConfigurationStatus.Approved, configuration, "approved configuration " + configuration.Version);

    public static BankTriageConfigurationResult NotConfigured(string detail) => new(BankTriageConfigurationStatus.NotConfigured, null, detail);

    public static BankTriageConfigurationResult Unavailable(string detail) => new(BankTriageConfigurationStatus.Unavailable, null, detail);
}

/// <summary>
/// Returns the bank's APPROVED configuration effective at <paramref name="asOf"/>. Implementations must return
/// configuration for the requested bank only (bank isolation) and never a default or another bank's values.
/// </summary>
public interface IBankTriageConfigurationProvider
{
    Task<BankTriageConfigurationResult> GetApprovedAsync(Guid bankId, DateTimeOffset asOf, CancellationToken cancellationToken);
}

/// <summary>Production default until ADR-0119 is approved: no bank has an approved configuration.</summary>
internal sealed class PendingApprovalBankTriageConfigurationProvider : IBankTriageConfigurationProvider
{
    public Task<BankTriageConfigurationResult> GetApprovedAsync(Guid bankId, DateTimeOffset asOf, CancellationToken cancellationToken) =>
        Task.FromResult(BankTriageConfigurationResult.NotConfigured(
            "BANK_CONFIGURATION_PENDING_APPROVAL: bank triage configuration storage is not approved (ADR-0119)."));
}
