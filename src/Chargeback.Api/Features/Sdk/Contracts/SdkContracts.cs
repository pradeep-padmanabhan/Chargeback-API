namespace Chargeback.Api.Features.Sdk.Contracts;

/// <summary>Opened after the backend validates the bank's signed host-app token (ADR-0005, pending).</summary>
public sealed record SdkSessionDto(Guid SessionId, Guid BankId, DateTimeOffset ExpiresAt);

public sealed record SdkTurnRequest(string Message);

/// <summary>AI-guided conversational turn. Advisory; eligibility is decided deterministically server-side.</summary>
public sealed record SdkTurnResponse(string AssistantMessage, bool Advisory, IReadOnlyDictionary<string, string> DraftFields);

/// <summary>If not eligible, <c>Reasons</c> is shown to the cardholder in the bank app and no dispute is created.</summary>
public sealed record SdkEligibilityResponse(bool Eligible, IReadOnlyList<string> Reasons);

public sealed record SdkSubmissionResponse(Guid DisputeId, string Status);
