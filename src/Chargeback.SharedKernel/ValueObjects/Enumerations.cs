namespace Chargeback.SharedKernel.ValueObjects;

// Member names match the baseline schema CHECK constraint values exactly.

/// <summary><c>triage_results.outcome</c>. A workflow outcome, never an AI decision.</summary>
public enum TriageOutcome
{
    ProceedToFiling,
    AutoRefund,
    RouteToHuman,
    SendToCompliance,
    Invalid,
    Defer,
}

/// <summary><c>documents.scheme_stage</c>: the scheme stage a document belongs to.</summary>
public enum DocumentStage
{
    Initial,
    PreArbitration,
    Arbitration,
}

/// <summary><c>documents.document_status</c>: processing status, independent of stage.</summary>
public enum DocumentStatus
{
    Pending,
    Processing,
    Success,
    Failed,
}
