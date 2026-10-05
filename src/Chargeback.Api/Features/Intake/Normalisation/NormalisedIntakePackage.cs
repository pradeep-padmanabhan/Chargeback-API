using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.SharedKernel.ValueObjects;

namespace Chargeback.Api.Features.Intake.Normalisation;

/// <summary>
/// The single shape every intake channel converges on (common guide §3 slice 1, §6 Act 1). Card data is
/// masked by construction. All facts are optional here: which are required is Gate 1's business rule.
/// </summary>
public sealed record NormalisedIntakePackage(
    Guid BankId,
    IntakeChannel Channel,
    string? CardholderReference,
    CardMasked? CardNumber,
    DateTimeOffset? TransactionDate,
    decimal? TransactionAmount,
    CurrencyCode? Currency,
    string? AcquirerReferenceNumber,
    string? MerchantName)
{
    /// <summary>Normalises a portal/API request. Call only after request validation succeeded.</summary>
    public static NormalisedIntakePackage FromRequest(SubmitDisputeRequest request, IntakeChannel channel)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new NormalisedIntakePackage(
            request.BankId,
            channel,
            Clean(request.CardholderReference),
            string.IsNullOrWhiteSpace(request.CardNumberMasked) ? null : CardMasked.Create(request.CardNumberMasked).Value,
            request.TransactionDate?.ToUniversalTime(),
            request.TransactionAmount,
            string.IsNullOrWhiteSpace(request.CurrencyCode) ? null : CurrencyCode.Create(request.CurrencyCode).Value,
            Clean(request.AcquirerReferenceNumber),
            Clean(request.MerchantName));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
