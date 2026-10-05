using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Api.Features.Intake.Normalisation;
using Chargeback.Api.Features.Intake.SubmitDispute;

namespace Chargeback.UnitTests.Intake;

public sealed class SubmitDisputeValidatorTests
{
    private static readonly Guid Bank = Guid.NewGuid();
    private readonly SubmitDisputeValidator _validator = new();

    private static SubmitDisputeRequest Request(
        string? card = "************1234", decimal? amount = 10.50m, string? currency = "GBP",
        string? cardholderRef = "CH-1", string? arn = "74537604221431003881552", string? merchant = "Shop") =>
        new(Bank, cardholderRef, card, DateTimeOffset.UtcNow, amount, currency, arn, merchant);

    [Fact]
    public void Well_formed_request_is_valid()
    {
        _validator.Validate(new SubmitDisputeCommand(Request())).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Missing_facts_are_not_a_validation_error_because_required_fields_is_gate_1()
    {
        var empty = new SubmitDisputeRequest(Bank, null, null, null, null, null, null, null);

        _validator.Validate(new SubmitDisputeCommand(empty)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Bank_id_is_required()
    {
        var result = _validator.Validate(new SubmitDisputeCommand(Request() with { BankId = Guid.Empty }));

        result.Errors.Should().Contain(e => e.PropertyName == "bankId");
    }

    [Theory]
    [InlineData("4111111111111111")]
    [InlineData("411111******1111")]
    [InlineData("1234")]
    public void Unmasked_or_malformed_card_is_rejected(string card)
    {
        var result = _validator.Validate(new SubmitDisputeCommand(Request(card: card)));

        result.Errors.Should().ContainSingle(e => e.PropertyName == "cardNumberMasked");
    }

    [Theory]
    [InlineData("cardholderReference")]
    [InlineData("merchantName")]
    [InlineData("acquirerReferenceNumber")]
    public void Card_number_in_any_free_text_field_is_rejected(string field)
    {
        const string pan = "customer paid with 4111 1111 1111 1111";
        var request = field switch
        {
            "cardholderReference" => Request(cardholderRef: pan),
            "merchantName" => Request(merchant: pan),
            _ => Request(arn: pan),
        };

        var result = _validator.Validate(new SubmitDisputeCommand(request));

        result.Errors.Should().Contain(e => e.PropertyName == field && e.ErrorMessage == SubmitDisputeValidator.ContainsCardNumber);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1.005)]
    public void Amount_must_fit_the_schema(double amount)
    {
        var result = _validator.Validate(new SubmitDisputeCommand(Request(amount: (decimal)amount)));

        result.Errors.Should().Contain(e => e.PropertyName == "transactionAmount");
    }

    [Theory]
    [InlineData("GB")]
    [InlineData("12A")]
    public void Currency_must_be_iso_format(string currency)
    {
        _validator.Validate(new SubmitDisputeCommand(Request(currency: currency))).Errors
            .Should().Contain(e => e.PropertyName == "currencyCode");
    }

    [Fact]
    public void Field_lengths_follow_the_schema()
    {
        var result = _validator.Validate(new SubmitDisputeCommand(Request(merchant: new string('m', 201))));

        result.Errors.Should().Contain(e => e.PropertyName == "merchantName");
    }

    [Fact]
    public void Normalisation_masks_and_trims()
    {
        var package = NormalisedIntakePackage.FromRequest(
            Request(card: "XXXX XXXX XXXX 1234", currency: "gbp", merchant: "  Shop  ", cardholderRef: " "),
            IntakeChannel.Portal);

        package.CardNumber!.Value.Should().Be("************1234");
        package.Currency!.Value.Should().Be("GBP");
        package.MerchantName.Should().Be("Shop");
        package.CardholderReference.Should().BeNull();
        package.Channel.Should().Be(IntakeChannel.Portal);
    }
}
