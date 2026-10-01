namespace Chargeback.Infrastructure.Ai;

/// <summary>Feature flags and limits for AI capabilities. Everything is off unless explicitly enabled.</summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>Global kill switch for this environment.</summary>
    public bool Enabled { get; set; }

    /// <summary>Capabilities enabled in this environment (names from <see cref="AiCapabilities"/>). Empty = none.</summary>
    public string[] EnabledCapabilities { get; set; } = [];

    /// <summary>Banks for which all AI capabilities are switched off.</summary>
    public Guid[] DisabledBankIds { get; set; } = [];

    /// <summary>Approved Bedrock model id (Q11: not yet confirmed).</summary>
    public string ModelId { get; set; } = "";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    public int MaxAttempts { get; set; } = 2;

    public int MaxOutputTokens { get; set; } = 2048;

    public bool IsEnabled(string capability, Guid? bankId) =>
        Enabled
        && EnabledCapabilities.Contains(capability, StringComparer.Ordinal)
        && (bankId is null || !DisabledBankIds.Contains(bankId.Value));
}
