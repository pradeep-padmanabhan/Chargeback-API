using Chargeback.Api.Features.Triage.Conditions;

namespace Chargeback.UnitTests.Triage;

public sealed class ConditionParserTests
{
    [Theory]
    [InlineData("""{"fact":"dispute.currencyCode","op":"eq","value":"GBP"}""")]
    [InlineData("""{"all":[{"fact":"dispute.transactionAmount","op":"gte","value":10},{"not":{"fact":"dispute.merchantName","op":"exists"}}]}""")]
    [InlineData("""{"any":[{"fact":"dispute.intakeChannel","op":"in","value":["PORTAL","API"]},{"fact":"dispute.transactionDate","op":"lt","value":"2026-01-31"}]}""")]
    [InlineData("""{"fact":"dispute.hasAcquirerReferenceNumber","op":"eq","value":true}""")]
    public void Well_formed_conditions_parse(string json)
    {
        ConditionParser.Parse(json).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("{}", "match every case")]                                                          // schema default must not mean "all"
    [InlineData("", "empty")]
    [InlineData("not json", "JSON")]
    [InlineData("""{"fact":"dispute.cardholderClaim","op":"eq","value":"x"}""", "catalogued fact")] // not in the catalogue
    [InlineData("""{"fact":"dispute.currencyCode","op":"gt","value":"GBP"}""", "number or date")]   // ordering on a string
    [InlineData("""{"fact":"dispute.transactionAmount","op":"eq","value":"100"}""", "Number")]      // type mismatch
    [InlineData("""{"fact":"dispute.transactionDate","op":"eq","value":"15/06/2026"}""", "Date")]
    [InlineData("""{"fact":"dispute.currencyCode","op":"exists","value":"GBP"}""", "takes no value")]
    [InlineData("""{"fact":"dispute.currencyCode","op":"in","value":[]}""", "non-empty array")]
    [InlineData("""{"fact":"dispute.currencyCode","op":"like","value":"G%"}""", "unsupported operator")]
    [InlineData("""{"fact":"dispute.currencyCode","op":"EQ","value":"GBP"}""", "unsupported operator")]
    [InlineData("""{"all":[]}""", "non-empty array")]
    [InlineData("""{"fact":"dispute.currencyCode","op":"eq","value":"GBP","weight":1}""", "unexpected member")]
    public void Malformed_conditions_are_invalid_rule_data(string json, string expected)
    {
        var result = ConditionParser.Parse(json);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_CONDITION");
        result.Error.Message.Should().Contain(expected);
    }

    [Fact]
    public void Excessive_nesting_is_rejected()
    {
        var json = string.Concat(Enumerable.Repeat("""{"not":""", 12)) + """{"fact":"dispute.currencyCode","op":"exists"}""" + new string('}', 12);

        ConditionParser.Parse(json).IsFailure.Should().BeTrue();
    }
}

public sealed class ConditionEvaluationTests
{
    private static TriState Eval(string json, CaseFacts facts) => ConditionParser.Parse(json).Value.Evaluate(facts);

    [Fact]
    public void Missing_fact_is_unknown_not_false()
    {
        Eval("""{"fact":"dispute.currencyCode","op":"eq","value":"GBP"}""", CaseFacts.Empty).Should().Be(TriState.Unknown);
        Eval("""{"fact":"dispute.currencyCode","op":"neq","value":"GBP"}""", CaseFacts.Empty).Should().Be(TriState.Unknown);
        Eval("""{"not":{"fact":"dispute.currencyCode","op":"eq","value":"GBP"}}""", CaseFacts.Empty).Should().Be(TriState.Unknown);
    }

    [Fact]
    public void Exists_is_always_decidable()
    {
        Eval("""{"fact":"dispute.currencyCode","op":"exists"}""", CaseFacts.Empty).Should().Be(TriState.False);
        Eval("""{"fact":"dispute.currencyCode","op":"notExists"}""", CaseFacts.Empty).Should().Be(TriState.True);
    }

    [Fact]
    public void All_and_any_use_three_valued_logic()
    {
        var facts = Synthetic.Facts();
        const string known = """{"fact":"dispute.currencyCode","op":"eq","value":"GBP"}""";
        const string unknown = """{"fact":"dispute.transactionDate","op":"exists"}""";
        const string missing = """{"fact":"scheme.reasonCode","op":"eq","value":"X"}""";

        Eval($$"""{"all":[{{known}},{{missing}}]}""", facts).Should().Be(TriState.Unknown);
        Eval($$"""{"all":[{"fact":"dispute.currencyCode","op":"eq","value":"EUR"},{{missing}}]}""", facts).Should().Be(TriState.False);
        Eval($$"""{"any":[{{known}},{{missing}}]}""", facts).Should().Be(TriState.True);
        Eval($$"""{"any":[{"fact":"dispute.currencyCode","op":"eq","value":"EUR"},{{missing}}]}""", facts).Should().Be(TriState.Unknown);
        Eval(unknown, facts).Should().Be(TriState.False);
    }

    [Theory]
    [InlineData("gte", 100, TriState.True)]
    [InlineData("gt", 100, TriState.False)]
    [InlineData("lt", 100.01, TriState.True)]
    [InlineData("lte", 99.99, TriState.False)]
    public void Numeric_comparisons_are_exact(string op, double value, TriState expected)
    {
        Eval($$"""{"fact":"dispute.transactionAmount","op":"{{op}}","value":{{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""", Synthetic.Facts(amount: 100m))
            .Should().Be(expected);
    }

    [Fact]
    public void Date_and_membership_conditions_evaluate()
    {
        var facts = Synthetic.Facts().With(FactCatalog.TransactionDate, FactValue.Of(new DateOnly(2026, 6, 15)));

        Eval("""{"fact":"dispute.transactionDate","op":"lte","value":"2026-06-15"}""", facts).Should().Be(TriState.True);
        Eval("""{"fact":"dispute.transactionDate","op":"lt","value":"2026-06-15"}""", facts).Should().Be(TriState.False);
        Eval("""{"fact":"dispute.currencyCode","op":"in","value":["EUR","GBP"]}""", facts).Should().Be(TriState.True);
        Eval("""{"fact":"dispute.currencyCode","op":"notIn","value":["EUR","GBP"]}""", facts).Should().Be(TriState.False);
    }

    [Fact]
    public void Facts_outside_the_catalogue_cannot_be_added()
    {
        var act = () => CaseFacts.Empty.With("dispute.pan", FactValue.Of("4111"));

        act.Should().Throw<ArgumentException>();
    }
}
