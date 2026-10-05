using System.Text.Json;
using Chargeback.Api.Features.Triage.Conditions;
using Chargeback.SharedKernel.Results;
using Microsoft.Extensions.Options;

namespace Chargeback.Api.Features.Triage.SchemeRules;

public enum SchemeEvaluationStatus
{
    /// <summary>Exactly one approved, effective rule matched and nothing was unknown.</summary>
    Determined,
    ConfigurationPending,
    NoApprovedRules,
    NoMatch,
    Ambiguous,
    IncompleteFacts,
    InvalidRuleData,

    /// <summary>Rules could not be read (database failure). Nothing may be decided or persisted.</summary>
    Unavailable,
}

public enum DeadlineStatus
{
    Calculated,
    NotApplicable,
    NoTimeLimitDefined,
    ConfigurationPending,
    MissingClockStartFact,
}

public enum RuleMatchResult
{
    Matched,
    NotMatched,
    Unknown,
    Invalid,
}

public sealed record RequiredDocument(string SlotName, bool IsRequired, string? ExpectedType);

public sealed record DeadlineResult(DeadlineStatus Status, DateOnly? ClockStartDate, DateOnly? DeadlineDate, int? TimeLimitDays)
{
    public static readonly DeadlineResult NotApplicable = new(DeadlineStatus.NotApplicable, null, null, null);
}

/// <summary>Audit entry for one approved, effective candidate rule.</summary>
public sealed record RuleCandidateAudit(Guid RuleSpecId, string ReasonCode, string Scenario, RuleMatchResult Result, string? Detail);

/// <summary>Why rules were not candidates (unapproved, not yet effective, expired, reason code not effective).</summary>
public sealed record RuleExclusions(int Draft, int Retired, int OtherStatus, int NotYetEffective, int Expired, int ReasonCodeNotEffective);

public sealed record DeterminedReasonCode(Guid Id, string Code, string Description, string? Category);

public sealed record SchemeRuleEvaluation(
    SchemeEvaluationStatus Status,
    string Detail,
    DateOnly? EvaluationDate,
    DeterminedReasonCode? ReasonCode,
    Guid? RuleSpecId,
    string? Scenario,
    DeadlineResult Deadline,
    IReadOnlyList<RequiredDocument> RequiredDocuments,
    IReadOnlyList<RuleCandidateAudit> Candidates,
    RuleExclusions? Exclusions)
{
    public bool IsDetermined => Status == SchemeEvaluationStatus.Determined;

    internal static SchemeRuleEvaluation NotDetermined(
        SchemeEvaluationStatus status,
        string detail,
        DateOnly? date = null,
        IReadOnlyList<RuleCandidateAudit>? candidates = null,
        RuleExclusions? exclusions = null) =>
        new(status, detail, date, null, null, null, DeadlineResult.NotApplicable, [], candidates ?? [], exclusions);
}

/// <summary>Input for one evaluation: facts plus the two timestamps a date basis may refer to.</summary>
public sealed record SchemeEvaluationInput(CaseFacts Facts, DateTimeOffset? TransactionDate, DateTimeOffset ReceivedAt);

public interface ISchemeRulesEngine
{
    TimeZoneInfo? Calendar { get; }

    Task<SchemeRuleEvaluation> EvaluateAsync(SchemeEvaluationInput input, CancellationToken cancellationToken);
}

/// <summary>
/// Deterministic scheme rules layer (common guide §6 Act 3). Only APPROVED rules whose rule and reason code
/// are both effective on the evaluation date are candidates (inclusive date bounds). A definitive result
/// requires exactly one matching candidate and no unknown or invalid candidate — anything else is an explicit
/// non-determined status. AI is never consulted.
/// </summary>
public sealed class SchemeRulesEngine(ISchemeRuleRepository repository, IOptions<SchemeRulesOptions> options) : ISchemeRulesEngine
{
    public const string Approved = "APPROVED";

    private readonly SchemeRulesOptions _options = options.Value;

    public TimeZoneInfo? Calendar => _options.ResolveCalendar();

    public async Task<SchemeRuleEvaluation> EvaluateAsync(SchemeEvaluationInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var calendar = Calendar;
        if (calendar is null || _options.EffectiveDateBasis is not { } effectiveBasis)
        {
            return SchemeRuleEvaluation.NotDetermined(
                SchemeEvaluationStatus.ConfigurationPending,
                "SchemeRules:CalendarTimeZone and SchemeRules:EffectiveDateBasis must be approved and configured (ADR-0122).");
        }

        if (DateFor(effectiveBasis, input, calendar) is not { } evaluationDate)
        {
            return SchemeRuleEvaluation.NotDetermined(
                SchemeEvaluationStatus.IncompleteFacts, $"The {effectiveBasis} needed to select effective rules is missing.");
        }

        IReadOnlyList<SchemeRuleSpecRecord> rules;
        try
        {
            rules = await repository.GetAllAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return SchemeRuleEvaluation.NotDetermined(SchemeEvaluationStatus.Unavailable, "Scheme rules could not be read.", evaluationDate);
        }

        var (candidates, exclusions) = SelectCandidates(rules, evaluationDate);
        if (candidates.Count == 0)
        {
            return SchemeRuleEvaluation.NotDetermined(
                SchemeEvaluationStatus.NoApprovedRules,
                "No APPROVED scheme rule is effective on the evaluation date.",
                evaluationDate,
                exclusions: exclusions);
        }

        var evaluated = candidates.Select(rule => (Rule: rule, Audit: Evaluate(rule, input.Facts))).ToArray();
        var audits = evaluated.Select(e => e.Audit.Audit).ToArray();

        if (audits.Any(a => a.Result == RuleMatchResult.Invalid))
        {
            return SchemeRuleEvaluation.NotDetermined(
                SchemeEvaluationStatus.InvalidRuleData, "At least one approved, effective rule has invalid rule data.", evaluationDate, audits, exclusions);
        }

        var matched = evaluated.Where(e => e.Audit.Audit.Result == RuleMatchResult.Matched).ToArray();
        if (matched.Length > 1)
        {
            return SchemeRuleEvaluation.NotDetermined(
                SchemeEvaluationStatus.Ambiguous, $"{matched.Length} approved rules match; exactly one is required.", evaluationDate, audits, exclusions);
        }

        if (audits.Any(a => a.Result == RuleMatchResult.Unknown))
        {
            return SchemeRuleEvaluation.NotDetermined(
                SchemeEvaluationStatus.IncompleteFacts, "Facts required by at least one candidate rule are missing.", evaluationDate, audits, exclusions);
        }

        if (matched.Length == 0)
        {
            return SchemeRuleEvaluation.NotDetermined(
                SchemeEvaluationStatus.NoMatch, "No approved, effective rule matches the case facts.", evaluationDate, audits, exclusions);
        }

        var (rule, result) = matched[0];
        return new SchemeRuleEvaluation(
            SchemeEvaluationStatus.Determined,
            "Exactly one approved, effective rule matched.",
            evaluationDate,
            new DeterminedReasonCode(rule.ReasonCodeId, rule.ReasonCode, rule.ReasonCodeDescription, rule.ReasonCodeCategory),
            rule.Id,
            rule.Scenario,
            CalculateDeadline(rule, input, calendar),
            result.Documents,
            audits,
            exclusions);
    }

    private static (List<SchemeRuleSpecRecord> Candidates, RuleExclusions Exclusions) SelectCandidates(
        IReadOnlyList<SchemeRuleSpecRecord> rules, DateOnly date)
    {
        int draft = 0, retired = 0, other = 0, notYet = 0, expired = 0, codeNotEffective = 0;
        var candidates = new List<SchemeRuleSpecRecord>();
        foreach (var rule in rules)
        {
            switch (rule.ApprovalStatus)
            {
                case "DRAFT":
                    draft++;
                    continue;
                case "RETIRED":
                    retired++;
                    continue;
                case Approved:
                    break;
                default:
                    other++;
                    continue;
            }

            if (date < rule.EffectiveFrom)
            {
                notYet++;
            }
            else if (rule.EffectiveTo is { } to && date > to)
            {
                expired++;
            }
            else if (date < rule.ReasonCodeEffectiveFrom || (rule.ReasonCodeEffectiveTo is { } codeTo && date > codeTo))
            {
                codeNotEffective++;
            }
            else
            {
                candidates.Add(rule);
            }
        }

        return (candidates, new RuleExclusions(draft, retired, other, notYet, expired, codeNotEffective));
    }

    private static (RuleCandidateAudit Audit, IReadOnlyList<RequiredDocument> Documents) Evaluate(SchemeRuleSpecRecord rule, CaseFacts facts)
    {
        var condition = ConditionParser.Parse(rule.ConditionsJson);
        if (condition.IsFailure)
        {
            return (Audit(rule, RuleMatchResult.Invalid, $"conditions_json: {condition.Error.Message}"), []);
        }

        var documents = RequiredDocumentsParser.Parse(rule.RequiredDocsJson);
        if (documents.IsFailure)
        {
            return (Audit(rule, RuleMatchResult.Invalid, $"required_docs: {documents.Error.Message}"), []);
        }

        return condition.Value.Evaluate(facts) switch
        {
            TriState.True => (Audit(rule, RuleMatchResult.Matched, null), documents.Value),
            TriState.False => (Audit(rule, RuleMatchResult.NotMatched, null), []),
            _ => (Audit(rule, RuleMatchResult.Unknown,
                "missing facts: " + string.Join(", ", condition.Value.Facts().Distinct().Where(f => !facts.TryGet(f, out _)))), []),
        };
    }

    private static RuleCandidateAudit Audit(SchemeRuleSpecRecord rule, RuleMatchResult result, string? detail) =>
        new(rule.Id, rule.ReasonCode, rule.Scenario, result, detail);

    private DeadlineResult CalculateDeadline(SchemeRuleSpecRecord rule, SchemeEvaluationInput input, TimeZoneInfo calendar)
    {
        if (rule.TimeLimitDays is not { } days)
        {
            return new DeadlineResult(DeadlineStatus.NoTimeLimitDefined, null, null, null);
        }

        if (_options.ClockStartBasis is not { } clockBasis || _options.DeadlineDayCounting is not DeadlineDayCounting.CalendarDays)
        {
            return new DeadlineResult(DeadlineStatus.ConfigurationPending, null, null, days);
        }

        return DateFor(clockBasis, input, calendar) is { } clockStart
            ? new DeadlineResult(DeadlineStatus.Calculated, clockStart, clockStart.AddDays(days), days)
            : new DeadlineResult(DeadlineStatus.MissingClockStartFact, null, null, days);
    }

    private static DateOnly? DateFor(SchemeDateBasis basis, SchemeEvaluationInput input, TimeZoneInfo calendar) => basis switch
    {
        SchemeDateBasis.TransactionDate => input.TransactionDate is { } t ? CaseFacts.ToDate(t, calendar) : null,
        SchemeDateBasis.DisputeReceivedDate => CaseFacts.ToDate(input.ReceivedAt, calendar),
        _ => null,
    };
}

/// <summary>
/// Parses <c>scheme_rule_specs.required_docs</c> (proposed format, ADR-0120):
/// <c>[{"slotName":"...","required":true,"expectedType":"..."}]</c>. Column limits follow <c>document_slots</c>.
/// </summary>
public static class RequiredDocumentsParser
{
    public static Result<IReadOnlyList<RequiredDocument>> Parse(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "null" : json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Invalid("must be a JSON array");
            }

            var result = new List<RequiredDocument>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || item.EnumerateObject().Any(p => p.Name is not ("slotName" or "required" or "expectedType"))
                    || !item.TryGetProperty("slotName", out var slot) || slot.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(slot.GetString()) || slot.GetString()!.Length > 150
                    || !item.TryGetProperty("required", out var required) || required.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return Invalid("each entry needs slotName (1-150 chars) and an explicit boolean 'required'");
                }

                string? expectedType = null;
                if (item.TryGetProperty("expectedType", out var type))
                {
                    if (type.ValueKind != JsonValueKind.String || type.GetString()!.Length > 80)
                    {
                        return Invalid("expectedType must be a string of at most 80 chars");
                    }

                    expectedType = type.GetString();
                }

                if (!names.Add(slot.GetString()!))
                {
                    return Invalid($"duplicate slotName '{slot.GetString()}'");
                }

                result.Add(new RequiredDocument(slot.GetString()!, required.GetBoolean(), expectedType));
            }

            return result;
        }
        catch (JsonException)
        {
            return Invalid("not valid JSON");
        }
    }

    private static Result<IReadOnlyList<RequiredDocument>> Invalid(string detail) =>
        Result.Failure<IReadOnlyList<RequiredDocument>>(Error.Failure("INVALID_REQUIRED_DOCS", detail));
}
