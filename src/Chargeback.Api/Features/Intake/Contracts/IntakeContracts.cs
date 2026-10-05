namespace Chargeback.Api.Features.Intake.Contracts;

/// <summary><c>disputes.intake_channel</c> (stored upper case).</summary>
public enum IntakeChannel
{
    Portal,
    Email,
    Bulk,
    Api,
    Sdk,
}

/// <summary>
/// Structured single-dispute intake (portal form or direct REST API). Card numbers are accepted
/// masked only (last four visible); full PAN is never accepted by this contract.
/// Channel is inferred server-side from the authenticated client (open question Q-new-5).
/// </summary>
public sealed record SubmitDisputeRequest(
    Guid BankId,
    string? CardholderReference,
    string? CardNumberMasked,
    DateTimeOffset? TransactionDate,
    decimal? TransactionAmount,
    string? CurrencyCode,
    string? AcquirerReferenceNumber,
    string? MerchantName);

/// <summary>Dispute persisted; the ten gates run and the status becomes NEW or FLAGGED.</summary>
public sealed record DisputeAcceptedResponse(Guid DisputeId, string Status);

/// <summary>Reads disputes and their gate trail (used by Intake endpoints and the Human Review workspace).</summary>
public interface IDisputeReader
{
    Task<DisputeDto?> ReadAsync(Guid disputeId, CancellationToken cancellationToken);

    /// <summary>Executed gates in gate order.</summary>
    Task<IReadOnlyList<GateResultDto>> ReadGatesAsync(Guid disputeId, CancellationToken cancellationToken);
}

public sealed record DisputeDto(
    Guid Id,
    Guid BankId,
    string? CardholderReference,
    string? CardNumberMasked,
    DateTimeOffset? TransactionDate,
    decimal? TransactionAmount,
    string? CurrencyCode,
    string? AcquirerReferenceNumber,
    string? MerchantName,
    IntakeChannel IntakeChannel,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// One executed gate (<c>gate_results</c>). <c>passed = null</c>: no decision; <c>executionStatus</c> says
/// whether the gate's criteria are pending definition or a technical error occurred (ADR-0117).
/// </summary>
public sealed record GateResultDto(
    int GateNumber,
    string GateName,
    bool? Passed,
    string? FlagReason,
    DateTimeOffset? CheckedAt,
    GateExecutionStatus ExecutionStatus);

/// <summary>How a gate execution ended. Only <see cref="Passed"/> counts as a successful validation.</summary>
public enum GateExecutionStatus
{
    Passed,
    Failed,
    PendingDefinition,
    TechnicalError,
}

public static class GateExecutionStatuses
{
    /// <summary>
    /// Recovers the status from stored columns: <c>passed</c> plus the reason-code prefix
    /// (<c>PENDING_DEFINITION:</c> / <c>GATE_ERROR:</c>). There is no status column (ADR-0117).
    /// An undecided row with an unrecognised reason is reported as a technical error, never as passed.
    /// </summary>
    public static GateExecutionStatus FromStored(bool? passed, string? flagReason) => passed switch
    {
        true => GateExecutionStatus.Passed,
        false => GateExecutionStatus.Failed,
        null when flagReason?.StartsWith("PENDING_DEFINITION", StringComparison.Ordinal) == true => GateExecutionStatus.PendingDefinition,
        null => GateExecutionStatus.TechnicalError,
    };
}

/// <summary>
/// <c>disputes.status</c> values defined by the approved process (common guide §6 Act 2). The full
/// status vocabulary is still pending (ADR-0110).
/// </summary>
public static class DisputeStatuses
{
    /// <summary>All ten gates passed; eligible for triage.</summary>
    public const string New = "NEW";

    /// <summary>At least one gate failed, is pending definition or errored; analyst queue. Never triaged automatically.</summary>
    public const string Flagged = "FLAGGED";
}

public sealed record BulkRowErrorDto(int RowNumber, string Field, string Code, string Message);

public sealed record BulkIntakeReportDto(bool DryRun, int TotalRows, int AcceptedRows, int RejectedRows, IReadOnlyList<BulkRowErrorDto> Errors);
