namespace Chargeback.SharedKernel.ValueObjects;

/// <summary>
/// Outcome of one executed intake validation gate (<c>gate_results</c>).
/// <see cref="Passed"/> is nullable to match the schema: <c>null</c> means the gate could not
/// reach a pass/fail decision (for example, its definition is pending business sign-off).
/// Gate definitions themselves live in the Intake slice registry, not here.
/// </summary>
public sealed record GateResult
{
    public const int MinGateNumber = 1;
    public const int MaxGateNumber = 10;
    public const int MaxGateNameLength = 100;

    public GateResult(int gateNumber, string gateName, bool? passed, string? flagReason, DateTimeOffset checkedAt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gateNumber, MinGateNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(gateNumber, MaxGateNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(gateName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(gateName.Length, MaxGateNameLength, nameof(gateName));

        GateNumber = gateNumber;
        GateName = gateName;
        Passed = passed;
        FlagReason = flagReason;
        CheckedAt = checkedAt;
    }

    public int GateNumber { get; }

    public string GateName { get; }

    public bool? Passed { get; }

    public string? FlagReason { get; }

    public DateTimeOffset CheckedAt { get; }
}
