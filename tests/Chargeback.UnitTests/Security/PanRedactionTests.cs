using Chargeback.Api.Common.Logging;
using Chargeback.SharedKernel.Security;
using Serilog.Events;
using Serilog.Parsing;

namespace Chargeback.UnitTests.Security;

public sealed class PanRedactionTests
{
    [Theory]
    [InlineData("card 4111111111111111 used", "card ************1111 used")]
    [InlineData("4111 1111 1111 1111", "************1111")]
    [InlineData("4111-1111-1111-1111", "************1111")]
    [InlineData("amex 3782 822463 10005", "amex ***********0005")]
    [InlineData("two 4111111111111111 and 5500005555555559", "two ************1111 and ************5559")]
    public void Redact_masks_pan_to_last_four(string input, string expected)
    {
        PanRedactor.Redact(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("ARN 74537604221431003881552")]                  // 23-digit ARN is not a PAN
    [InlineData("amount 1234.56 on 2026-09-29")]
    [InlineData("case CB-2026-000123")]
    [InlineData("************1111")]
    public void Redact_leaves_non_pan_text_unchanged(string input)
    {
        PanRedactor.ContainsPan(input).Should().BeFalse();
        PanRedactor.Redact(input).Should().Be(input);
    }

    [Fact]
    public void Enricher_masks_scalar_and_nested_properties()
    {
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            new MessageTemplate([new TextToken("x")]),
            [
                new LogEventProperty("Plain", new ScalarValue("4111111111111111")),
                new LogEventProperty("Nested", new StructureValue([new LogEventProperty("Pan", new ScalarValue("pan 5500005555555559"))])),
                new LogEventProperty("List", new SequenceValue([new ScalarValue("4111 1111 1111 1111"), new ScalarValue(42)])),
                new LogEventProperty("Safe", new ScalarValue("nothing here")),
            ]);

        new PanRedactionEnricher().Enrich(logEvent, Substitute.For<Serilog.Core.ILogEventPropertyFactory>());

        var rendered = string.Join("|", logEvent.Properties.Select(p => p.Value.ToString()));
        rendered.Should().NotContain("4111111111111111").And.NotContain("5500005555555559").And.NotContain("4111 1111 1111 1111");
        rendered.Should().Contain("************1111").And.Contain("************5559").And.Contain("nothing here");
    }
}
