using Chargeback.SharedKernel.Entities;
using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.Infrastructure.Persistence.Entities;

/// <summary><c>scheme_reason_codes</c></summary>
public sealed class SchemeReasonCode : AuditableEntity
{
    public required string Code { get; set; }

    public required string Description { get; set; }

    public string? Category { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }
}

/// <summary><c>scheme_rule_specs</c></summary>
public sealed class SchemeRuleSpec : AuditableEntity
{
    public Guid ReasonCodeId { get; set; }

    public required string Scenario { get; set; }

    /// <summary>jsonb</summary>
    public string ConditionsJson { get; set; } = "{}";

    /// <summary>jsonb</summary>
    public string RequiredDocs { get; set; } = "[]";

    public int? TimeLimitDays { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public string ApprovalStatus { get; set; } = "DRAFT";
}

/// <summary><c>disputes</c></summary>
public sealed class Dispute : AuditableEntity
{
    public Guid BankId { get; set; }

    public string? CardholderReference { get; set; }

    public string? CardNumberMasked { get; set; }

    public DateTimeOffset? TransactionDate { get; set; }

    public decimal? TransactionAmount { get; set; }

    public string? CurrencyCode { get; set; }

    public string? AcquirerReferenceNumber { get; set; }

    public string? MerchantName { get; set; }

    /// <summary>PORTAL | EMAIL | BULK | API | SDK</summary>
    public required string IntakeChannel { get; set; }

    public string Status { get; set; } = "NEW";
}

/// <summary><c>gate_results</c></summary>
public sealed class GateResultRecord : BaseEntity
{
    public Guid DisputeId { get; set; }

    public int GateNumber { get; set; }

    public required string GateName { get; set; }

    public bool? Passed { get; set; }

    public string? FlagReason { get; set; }

    public DateTimeOffset? CheckedAt { get; set; }
}

/// <summary><c>cases</c></summary>
public sealed class Case : AuditableEntity
{
    public Guid DisputeId { get; set; }

    public required string CaseReference { get; set; }

    public Guid? AssignedTo { get; set; }

    public string? Priority { get; set; }

    public string Status { get; set; } = "NEW";

    /// <summary>Column <c>derived_reason_code</c> (FK to scheme_reason_codes.id).</summary>
    public Guid? DerivedReasonCodeId { get; set; }

    public DateOnly? ClockStartDate { get; set; }

    public DateOnly? FilingDeadlineDate { get; set; }

    /// <summary>Stored by the schema but stale by nature; the API computes it on read (ADR-0115).</summary>
    public int? DaysRemaining { get; set; }

    public string? SchemeFunctionCode { get; set; }

    public string? AiSummary { get; set; }

    public string? HumanReviewVerdict { get; set; }

    public Guid? HumanReviewedBy { get; set; }

    public DateTimeOffset? HumanReviewedAt { get; set; }
}

/// <summary><c>triage_results</c></summary>
public sealed class TriageResultRecord : BaseEntity, IHasCreatedAt
{
    public Guid CaseId { get; set; }

    public string? TriageLayer { get; set; }

    public bool? HardEligibilityPass { get; set; }

    public string? RoutingPolicyOutcome { get; set; }

    public decimal? RiskScore { get; set; }

    /// <summary>jsonb</summary>
    public string RiskFlags { get; set; } = "[]";

    public bool HumanReviewTriggered { get; set; }

    public string? HumanReviewReason { get; set; }

    public TriageOutcome? Outcome { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary><c>document_slots</c></summary>
public sealed class DocumentSlot : AuditableEntity
{
    public Guid CaseId { get; set; }

    public required string SlotName { get; set; }

    public bool IsRequired { get; set; }

    public string? ExpectedType { get; set; }
}

/// <summary><c>documents</c></summary>
public sealed class Document : AuditableEntity
{
    public Guid CaseId { get; set; }

    public Guid? DocumentSlotId { get; set; }

    public required string FileName { get; set; }

    public required string S3Key { get; set; }

    public string? MimeType { get; set; }

    public long? FileSizeBytes { get; set; }

    public string? TextractJobId { get; set; }

    public string? ExtractedText { get; set; }

    public string? AiClassification { get; set; }

    public decimal? AiConfidence { get; set; }

    public DocumentStage SchemeStage { get; set; }

    public DocumentStatus DocumentStatus { get; set; } = DocumentStatus.Pending;

    public string? UploadSource { get; set; }

    public Guid? UploadedBy { get; set; }

    public DateTimeOffset UploadedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? ProcessingError { get; set; }
}
