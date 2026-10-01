using Chargeback.Infrastructure.Persistence.Queries;

namespace Chargeback.Api.Features.Triage.SchemeRules;

/// <summary>One <c>scheme_rule_specs</c> row joined with its <c>scheme_reason_codes</c> row.</summary>
public sealed record SchemeRuleSpecRecord(
    Guid Id,
    Guid ReasonCodeId,
    string Scenario,
    string ConditionsJson,
    string RequiredDocsJson,
    int? TimeLimitDays,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string ApprovalStatus,
    string ReasonCode,
    string ReasonCodeDescription,
    string? ReasonCodeCategory,
    DateOnly ReasonCodeEffectiveFrom,
    DateOnly? ReasonCodeEffectiveTo);

public interface ISchemeRuleRepository
{
    /// <summary>All rule specs in every approval state; the engine filters (so exclusions can be audited).</summary>
    Task<IReadOnlyList<SchemeRuleSpecRecord>> GetAllAsync(CancellationToken cancellationToken);
}

internal sealed class SchemeRuleRepository(IDapperQueryService db) : ISchemeRuleRepository
{
    private const string Sql = """
        SELECT s.id, s.reason_code_id, s.scenario, s.conditions_json::text AS conditions_json,
               s.required_docs::text AS required_docs_json, s.time_limit_days, s.effective_from, s.effective_to,
               s.approval_status, rc.code AS reason_code, rc.description AS reason_code_description,
               rc.category AS reason_code_category, rc.effective_from AS reason_code_effective_from,
               rc.effective_to AS reason_code_effective_to
        FROM chargeback_diagram.scheme_rule_specs s
        JOIN chargeback_diagram.scheme_reason_codes rc ON rc.id = s.reason_code_id
        ORDER BY s.id
        """;

    public async Task<IReadOnlyList<SchemeRuleSpecRecord>> GetAllAsync(CancellationToken cancellationToken) =>
        (await db.QueryAsync<Row>(Sql, null, cancellationToken)).Select(r => r.ToRecord()).ToArray();

    private sealed class Row
    {
        public Guid Id { get; set; }

        public Guid ReasonCodeId { get; set; }

        public string Scenario { get; set; } = "";

        public string ConditionsJson { get; set; } = "";

        public string RequiredDocsJson { get; set; } = "";

        public int? TimeLimitDays { get; set; }

        public DateOnly EffectiveFrom { get; set; }

        public DateOnly? EffectiveTo { get; set; }

        public string ApprovalStatus { get; set; } = "";

        public string ReasonCode { get; set; } = "";

        public string ReasonCodeDescription { get; set; } = "";

        public string? ReasonCodeCategory { get; set; }

        public DateOnly ReasonCodeEffectiveFrom { get; set; }

        public DateOnly? ReasonCodeEffectiveTo { get; set; }

        public SchemeRuleSpecRecord ToRecord() => new(
            Id, ReasonCodeId, Scenario, ConditionsJson, RequiredDocsJson, TimeLimitDays, EffectiveFrom, EffectiveTo,
            ApprovalStatus, ReasonCode, ReasonCodeDescription, ReasonCodeCategory, ReasonCodeEffectiveFrom, ReasonCodeEffectiveTo);
    }
}
