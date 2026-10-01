using Chargeback.Api.Features.Triage.SchemeRules;
using Microsoft.Extensions.Options;

namespace Chargeback.UnitTests.Triage;

/// <summary>Engine mechanics with SYNTHETIC rules only (see <see cref="Synthetic"/>).</summary>
public sealed class SchemeRulesEngineTests
{
    private static SchemeRulesEngine Engine(ISchemeRuleRepository repository, SchemeRulesOptions? options = null) =>
        new(repository, Options.Create(options ?? Synthetic.FullOptions()));

    private static Task<SchemeRuleEvaluation> Evaluate(params SchemeRuleSpecRecord[] rules) =>
        Engine(new InMemoryRuleRepository(rules)).EvaluateAsync(Synthetic.Input(), CancellationToken.None);

    [Fact]
    public async Task Exactly_one_matching_approved_rule_determines_code_deadline_and_documents()
    {
        var rule = Synthetic.Rule(timeLimitDays: 30);

        var result = await Evaluate(rule, Synthetic.Rule(code: "SYN-EUR", conditions: """{"fact":"dispute.currencyCode","op":"eq","value":"EUR"}"""));

        result.Status.Should().Be(SchemeEvaluationStatus.Determined);
        result.ReasonCode!.Code.Should().Be("SYN-001");
        result.RuleSpecId.Should().Be(rule.Id);
        result.EvaluationDate.Should().Be(Synthetic.Day);
        result.Deadline.Should().Be(new DeadlineResult(DeadlineStatus.Calculated, Synthetic.Day, Synthetic.Day.AddDays(30), 30));
        result.RequiredDocuments.Should().Equal(new RequiredDocument("SYN letter", true, "SYN-LETTER"));
        result.Candidates.Should().HaveCount(2).And.Contain(c => c.Result == RuleMatchResult.NotMatched);
    }

    [Fact]
    public async Task Missing_configuration_is_pending_never_a_guess()
    {
        var result = await Engine(new InMemoryRuleRepository(Synthetic.Rule()), new SchemeRulesOptions())
            .EvaluateAsync(Synthetic.Input(), CancellationToken.None);

        result.Status.Should().Be(SchemeEvaluationStatus.ConfigurationPending);
        result.ReasonCode.Should().BeNull();
    }

    [Fact]
    public async Task Invalid_time_zone_is_configuration_pending()
    {
        var options = Synthetic.FullOptions();
        options.CalendarTimeZone = "Not/AZone";

        (await Engine(new InMemoryRuleRepository(Synthetic.Rule()), options).EvaluateAsync(Synthetic.Input(), CancellationToken.None))
            .Status.Should().Be(SchemeEvaluationStatus.ConfigurationPending);
    }

    [Fact]
    public async Task Missing_effective_date_fact_is_incomplete()
    {
        var input = Synthetic.Input() with { TransactionDate = null };

        (await Engine(new InMemoryRuleRepository(Synthetic.Rule())).EvaluateAsync(input, CancellationToken.None))
            .Status.Should().Be(SchemeEvaluationStatus.IncompleteFacts);
    }

    [Fact]
    public async Task Unapproved_rules_are_never_used_and_are_counted()
    {
        var result = await Evaluate(Synthetic.Rule(status: "DRAFT"), Synthetic.Rule(status: "RETIRED"), Synthetic.Rule(status: "PENDING"));

        result.Status.Should().Be(SchemeEvaluationStatus.NoApprovedRules);
        result.Exclusions.Should().Be(new RuleExclusions(1, 1, 1, 0, 0, 0));
    }

    [Theory]
    [InlineData("2026-06-15", null, true)]          // effective_from == evaluation date: included
    [InlineData("2026-06-16", null, false)]         // starts tomorrow: not yet effective
    [InlineData("2026-01-01", "2026-06-15", true)]  // effective_to == evaluation date: included (inclusive)
    [InlineData("2026-01-01", "2026-06-14", false)] // ended the day before: expired
    public async Task Effective_date_bounds_are_inclusive(string from, string? to, bool expectedDetermined)
    {
        var rule = Synthetic.Rule(from: DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            to: to is null ? null : DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture));

        var result = await Evaluate(rule);

        (result.Status == SchemeEvaluationStatus.Determined).Should().Be(expectedDetermined);
    }
    [Fact]
    public async Task Exclusions_distinguish_not_yet_effective_expired_and_reason_code_not_effective()
    {
        var result = await Evaluate(
            Synthetic.Rule(from: new DateOnly(2026, 6, 16)),
            Synthetic.Rule(to: new DateOnly(2026, 6, 14)),
            Synthetic.Rule(codeTo: new DateOnly(2026, 6, 1)));

        result.Status.Should().Be(SchemeEvaluationStatus.NoApprovedRules);
        result.Exclusions.Should().Be(new RuleExclusions(0, 0, 0, 1, 1, 1));
    }

    [Fact]
    public async Task Version_selection_uses_the_version_effective_on_the_evaluation_date()
    {
        var v1 = Synthetic.Rule(code: "SYN-001", timeLimitDays: 30, from: new DateOnly(2025, 1, 1), to: new DateOnly(2026, 5, 31));
        var v2 = Synthetic.Rule(code: "SYN-001", timeLimitDays: 45, from: new DateOnly(2026, 6, 1));

        var result = await Evaluate(v1, v2);

        result.RuleSpecId.Should().Be(v2.Id);
        result.Deadline.TimeLimitDays.Should().Be(45);
        result.Exclusions!.Expired.Should().Be(1);

        var earlier = await Engine(new InMemoryRuleRepository(v1, v2))
            .EvaluateAsync(Synthetic.Input(transactionDay: new DateOnly(2026, 5, 31)), CancellationToken.None);
        earlier.RuleSpecId.Should().Be(v1.Id);
    }

    [Fact]
    public async Task Overlapping_approved_versions_are_ambiguous()
    {
        var result = await Evaluate(Synthetic.Rule(code: "SYN-001"), Synthetic.Rule(code: "SYN-002"));

        result.Status.Should().Be(SchemeEvaluationStatus.Ambiguous);
        result.ReasonCode.Should().BeNull();
    }

    [Fact]
    public async Task No_matching_rule_is_no_match()
    {
        (await Evaluate(Synthetic.Rule(conditions: """{"fact":"dispute.currencyCode","op":"eq","value":"EUR"}""")))
            .Status.Should().Be(SchemeEvaluationStatus.NoMatch);
    }

    [Fact]
    public async Task A_rule_over_a_missing_fact_blocks_determination()
    {
        var needsDate = Synthetic.Rule(code: "SYN-DATE", conditions: """{"fact":"dispute.receivedDate","op":"gt","value":"2026-01-01"}""");

        var result = await Evaluate(Synthetic.Rule(), needsDate);

        result.Status.Should().Be(SchemeEvaluationStatus.IncompleteFacts);
        result.Candidates.Single(c => c.ReasonCode == "SYN-DATE").Detail.Should().Contain("dispute.receivedDate");
    }

    [Theory]
    [InlineData("{}", "[]")]                                                                    // column default conditions
    [InlineData("""{"fact":"dispute.currencyCode","op":"eq","value":"GBP"}""", """[{"slotName":"x"}]""")] // docs without 'required'
    [InlineData("""{"fact":"dispute.currencyCode","op":"eq","value":"GBP"}""", "{}")]
    public async Task Invalid_rule_data_blocks_determination(string conditions, string docs)
    {
        var result = await Evaluate(Synthetic.Rule(code: "SYN-OK", conditions: """{"fact":"dispute.currencyCode","op":"eq","value":"EUR"}"""), Synthetic.Rule(conditions: conditions, docs: docs));

        result.Status.Should().Be(SchemeEvaluationStatus.InvalidRuleData);
        result.Candidates.Should().Contain(c => c.Result == RuleMatchResult.Invalid);
    }

    [Fact]
    public async Task Rule_without_time_limit_has_no_deadline()
    {
        (await Evaluate(Synthetic.Rule(timeLimitDays: null))).Deadline.Status.Should().Be(DeadlineStatus.NoTimeLimitDefined);
    }

    [Fact]
    public async Task Deadline_needs_clock_start_configuration()
    {
        var options = Synthetic.FullOptions();
        options.ClockStartBasis = null;

        var result = await Engine(new InMemoryRuleRepository(Synthetic.Rule()), options).EvaluateAsync(Synthetic.Input(), CancellationToken.None);

        result.Status.Should().Be(SchemeEvaluationStatus.Determined);
        result.Deadline.Status.Should().Be(DeadlineStatus.ConfigurationPending);
        result.Deadline.DeadlineDate.Should().BeNull();
    }

    [Fact]
    public async Task Deadline_uses_the_configured_clock_basis_in_calendar_days()
    {
        var options = Synthetic.FullOptions();
        options.EffectiveDateBasis = SchemeDateBasis.DisputeReceivedDate;
        options.ClockStartBasis = SchemeDateBasis.DisputeReceivedDate;

        var result = await Engine(new InMemoryRuleRepository(Synthetic.Rule(timeLimitDays: 15)), options).EvaluateAsync(Synthetic.Input(), CancellationToken.None);

        result.Deadline.ClockStartDate.Should().Be(new DateOnly(2026, 6, 20));
        result.Deadline.DeadlineDate.Should().Be(new DateOnly(2026, 7, 5));
    }

    [Fact]
    public async Task Missing_clock_start_fact_is_reported()
    {
        var options = Synthetic.FullOptions();
        options.EffectiveDateBasis = SchemeDateBasis.DisputeReceivedDate;
        var input = Synthetic.Input() with { TransactionDate = null };

        var result = await Engine(new InMemoryRuleRepository(Synthetic.Rule()), options).EvaluateAsync(input, CancellationToken.None);

        result.Deadline.Status.Should().Be(DeadlineStatus.MissingClockStartFact);
    }

    [Fact]
    public async Task Repository_failure_is_unavailable_not_a_decision()
    {
        var repository = new InMemoryRuleRepository(Synthetic.Rule()) { Failure = new InvalidOperationException("database down") };

        var result = await Engine(repository).EvaluateAsync(Synthetic.Input(), CancellationToken.None);

        result.Status.Should().Be(SchemeEvaluationStatus.Unavailable);
        result.ReasonCode.Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var repository = new InMemoryRuleRepository() { Failure = new OperationCanceledException(cts.Token) };

        var act = () => Engine(repository).EvaluateAsync(Synthetic.Input(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("""[]""", 0)]
    [InlineData("""[{"slotName":"A","required":true},{"slotName":"B","required":false,"expectedType":"T"}]""", 2)]
    public void Required_documents_parse(string json, int count)
    {
        RequiredDocumentsParser.Parse(json).Value.Should().HaveCount(count);
    }

    [Theory]
    [InlineData("""[{"slotName":"A","required":true},{"slotName":"A","required":true}]""")]
    [InlineData("""[{"slotName":"","required":true}]""")]
    [InlineData("""[{"slotName":"A","required":"yes"}]""")]
    [InlineData("""[{"slotName":"A","required":true,"extra":1}]""")]
    public void Required_documents_reject_malformed_entries(string json)
    {
        RequiredDocumentsParser.Parse(json).IsFailure.Should().BeTrue();
    }
}
