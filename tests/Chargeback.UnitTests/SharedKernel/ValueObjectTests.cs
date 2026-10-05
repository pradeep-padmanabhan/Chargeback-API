using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.UnitTests.SharedKernel;

public sealed class CardMaskedTests
{
    [Theory]
    [InlineData("************1234", "************1234")]
    [InlineData("XXXX XXXX XXXX 1234", "************1234")]
    [InlineData("xxxx-xxxx-xxxx-9876", "************9876")]
    [InlineData("•••••••••••4321", "***********4321")]
    public void Create_accepts_last_four_only_masks(string input, string expected)
    {
        var result = CardMasked.Create(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(expected);
        result.Value.Last4.Should().Be(expected[^4..]);
    }

    [Theory]
    [InlineData("4111111111111111")]            // full PAN
    [InlineData("411111******1111")]            // BIN + last four: more than four digits visible
    [InlineData("****1234")]                    // too short
    [InlineData("************12a4")]
    [InlineData("")]
    [InlineData(null)]
    public void Create_rejects_anything_exposing_more_than_last_four(string? input)
    {
        CardMasked.Create(input).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData("4111111111111111", "************1111")]
    [InlineData("4111 1111 1111 1111", "************1111")]
    [InlineData("378282246310005", "***********0005")]
    public void FromPan_masks_to_last_four(string pan, string expected)
    {
        CardMasked.FromPan(pan).Value.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData("41111111")]
    [InlineData("41111111111111111111")]
    [InlineData("4111-1111-1111-111A")]
    public void FromPan_rejects_invalid_input(string pan)
    {
        CardMasked.FromPan(pan).IsFailure.Should().BeTrue();
    }
}

public sealed class MoneyTests
{
    private static readonly CurrencyCode Gbp = CurrencyCode.Create("GBP").Value;
    private static readonly CurrencyCode Eur = CurrencyCode.Create("EUR").Value;

    [Theory]
    [InlineData("0")]
    [InlineData("10.5")]
    [InlineData("10.50")]
    [InlineData("9999999999999999.99")]
    public void Create_accepts_schema_precision(string amount)
    {
        Money.Create(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), Gbp).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_never_rounds_silently()
    {
        Money.Create(10.555m, Gbp).Error.Code.Should().Be("MONEY_SCALE_INVALID");
    }

    [Fact]
    public void Create_rejects_values_beyond_numeric_18_2()
    {
        Money.Create(10_000_000_000_000_000m, Gbp).Error.Code.Should().Be("MONEY_OUT_OF_RANGE");
    }

    [Fact]
    public void Add_requires_same_currency()
    {
        var a = Money.Create(1.10m, Gbp).Value;

        a.Add(Money.Create(2.20m, Gbp).Value).Value.Amount.Should().Be(3.30m);
        a.Add(Money.Create(1m, Eur).Value).Error.Code.Should().Be("MONEY_CURRENCY_MISMATCH");
    }
}

public sealed class CurrencyCodeAndReasonCodeTests
{
    [Theory]
    [InlineData("gbp", "GBP")]
    [InlineData(" EUR ", "EUR")]
    public void CurrencyCode_normalizes_format(string input, string expected)
    {
        CurrencyCode.Create(input).Value.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData("GB")]
    [InlineData("GBPX")]
    [InlineData("G1P")]
    [InlineData(null)]
    public void CurrencyCode_rejects_bad_format(string? input)
    {
        CurrencyCode.Create(input).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData("4853")]
    [InlineData("10.4")]
    public void ReasonCode_accepts_format_only_values(string code)
    {
        ReasonCode.Create(code).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345678901234567")]
    [InlineData("48 53")]
    public void ReasonCode_rejects_bad_format(string code)
    {
        ReasonCode.Create(code).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void GateResult_enforces_schema_gate_number_range(int gateNumber)
    {
        var act = () => new GateResult(gateNumber, "Gate", true, null, DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GateResult_allows_undecided_outcome()
    {
        new GateResult(1, "Required Fields", null, "PENDING_DEFINITION", DateTimeOffset.UtcNow).Passed.Should().BeNull();
    }

    [Fact]
    public void Enum_member_names_match_schema_check_constraints()
    {
        Enum.GetNames<TriageOutcome>().Should().Equal("ProceedToFiling", "AutoRefund", "RouteToHuman", "SendToCompliance", "Invalid", "Defer");
        Enum.GetNames<DocumentStage>().Should().Equal("Initial", "PreArbitration", "Arbitration");
        Enum.GetNames<DocumentStatus>().Should().Equal("Pending", "Processing", "Success", "Failed");
    }
}
