using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Api.Features.Intake.SubmitDispute;
using Chargeback.SharedKernel.Security;

namespace Chargeback.UnitTests.Security;

/// <summary>
/// Regression for the Phase 6 false positive: free text with a 13–19 digit run was rejected even when it could
/// not be a card number. Rejection now also requires the Luhn check; masking stays conservative.
/// The "must pass" numbers were verified to FAIL Luhn; the "must fail" numbers are published industry test PANs.
/// </summary>
public sealed class PanLuhnRegressionTests
{
    public static TheoryData<string> MustBeAccepted => new()
    {
        "Store ref 1234567890123",            // 13-digit store reference, fails Luhn
        "Loyalty no. 9876543210987654",       // 16-digit loyalty number, fails Luhn
        "Branch 20260929000123",              // 14-digit date-based reference, fails Luhn
        "Member 7000123456789012",            // 16 digits, fails Luhn
        "Order 9876543212345678901",          // 19 digits, fails Luhn
        "Invoice 5000 0000 0000 0001",        // grouped like a card, fails Luhn
    };

    public static TheoryData<string> MustBeRejected => new()
    {
        "4111111111111111",                   // Visa test PAN
        "paid with 5500005555555559",         // Mastercard test PAN
        "amex 378282246310005",               // Amex test PAN (15 digits)
        "4012 8888 8888 1881",                // grouped Visa test PAN
        "5105-1051-0510-5100",                // dashed Mastercard test PAN
        "card 6011111111111117 used",         // Discover test PAN
        "3782 822463 10005",                  // Amex 4-6-5 grouping
    };

    [Theory]
    [MemberData(nameof(MustBeAccepted))]
    public void Non_luhn_digit_runs_are_not_treated_as_card_numbers(string text)
    {
        PanRedactor.ContainsPan(text).Should().BeFalse();
        PanRedactor.ContainsPanCandidate(text).Should().BeTrue("the text still looks PAN-shaped");
    }

    [Theory]
    [MemberData(nameof(MustBeRejected))]
    public void Luhn_valid_test_pans_are_detected(string text)
    {
        PanRedactor.ContainsPan(text).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(MustBeAccepted))]
    public void Masking_remains_conservative_for_non_luhn_runs(string text)
    {
        PanRedactor.Redact(text).Should().Contain("****", "logs and AI input mask every PAN-shaped run");
    }

    [Theory]
    [MemberData(nameof(MustBeAccepted))]
    public void Intake_accepts_long_references_in_free_text(string text)
    {
        var request = new SubmitDisputeRequest(Guid.NewGuid(), text, null, null, null, null, null, text);

        new SubmitDisputeValidator().Validate(new SubmitDisputeCommand(request)).IsValid.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(MustBeRejected))]
    public void Intake_rejects_test_pans_in_free_text(string text)
    {
        var request = new SubmitDisputeRequest(Guid.NewGuid(), null, null, null, null, null, null, "Shop " + text);

        new SubmitDisputeValidator().Validate(new SubmitDisputeCommand(request)).Errors
            .Should().Contain(e => e.PropertyName == "merchantName" && e.ErrorMessage == SubmitDisputeValidator.ContainsCardNumber);
    }

    [Theory]
    [InlineData("4111111111111111", true)]
    [InlineData("4111111111111112", false)]
    [InlineData("0", true)]
    [InlineData("", false)]
    [InlineData("41a1", false)]
    public void Luhn_check_is_standard(string digits, bool expected)
    {
        PanRedactor.PassesLuhn(digits).Should().Be(expected);
    }
}
