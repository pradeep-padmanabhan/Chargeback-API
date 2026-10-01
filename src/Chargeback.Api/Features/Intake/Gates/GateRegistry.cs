using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.Api.Features.Intake.Gates;

/// <summary>One gate in the approved registry: position and display name only. Criteria live in evaluators.</summary>
public sealed record GateDefinition(int Number, string Name);

/// <summary>The ordered set of intake validation gates and the registry version used to evaluate a dispute.</summary>
public interface IGateRegistry
{
    string Version { get; }

    IReadOnlyList<GateDefinition> Gates { get; }
}

/// <summary>
/// The ten gates approved from the updated ER diagram (common guide §5 / §6 "Resolved gate-list conflict").
/// Names and order only: no thresholds, applicability or failure policies are defined here, because
/// none have been approved yet. The earlier process-flow labels (duplicate Gate 9 etc.) are deliberately not used.
/// </summary>
public sealed class ApprovedGateRegistry : IGateRegistry
{
    public const string RegistryVersion = "erd-baseline-1.2";

    public ApprovedGateRegistry()
        : this(RegistryVersion,
        [
            new(1, "Required Fields"),
            new(2, "Transaction Lookup"),
            new(3, "Card/Account Check"),
            new(4, "Amount/Currency Check"),
            new(5, "Time Window Check"),
            new(6, "Duplicate Check"),
            new(7, "Merchant/Category Check"),
            new(8, "Reason Code Derivation"),
            new(9, "Document Requirement"),
            new(10, "Compliance Check"),
        ])
    {
    }

    internal ApprovedGateRegistry(string version, IReadOnlyList<GateDefinition> gates)
    {
        Validate(gates);
        Version = version;
        Gates = gates;
    }

    public string Version { get; }

    public IReadOnlyList<GateDefinition> Gates { get; }

    /// <summary>Exactly ten gates numbered 1..10 in order, each with a schema-compatible name.</summary>
    internal static void Validate(IReadOnlyList<GateDefinition> gates)
    {
        ArgumentNullException.ThrowIfNull(gates);

        if (gates.Count != GateResult.MaxGateNumber)
        {
            throw new InvalidOperationException($"The gate registry must contain exactly {GateResult.MaxGateNumber} gates.");
        }

        for (var i = 0; i < gates.Count; i++)
        {
            if (gates[i].Number != i + 1)
            {
                throw new InvalidOperationException("Gates must be numbered 1..10 in evaluation order.");
            }

            if (string.IsNullOrWhiteSpace(gates[i].Name) || gates[i].Name.Length > GateResult.MaxGateNameLength)
            {
                throw new InvalidOperationException($"Gate {gates[i].Number} needs a name of 1-{GateResult.MaxGateNameLength} characters.");
            }
        }
    }
}
