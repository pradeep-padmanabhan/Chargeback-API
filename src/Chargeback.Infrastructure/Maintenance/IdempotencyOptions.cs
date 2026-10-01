namespace Chargeback.Infrastructure.Maintenance;

/// <summary>Configuration section <c>Idempotency</c> (ADR-0106, approved).</summary>
public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>Approved: stored idempotency results expire 90 days after creation.</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Approved: consumed-event records (<c>processed_domain_events</c>) are purged after 90 days in the same run.</summary>
    public TimeSpan ProcessedEventRetention { get; set; } = TimeSpan.FromDays(90);

    public bool PurgeEnabled { get; set; } = true;

    /// <summary>Nightly run time, UTC, <c>HH:mm</c>.</summary>
    public string PurgeTimeUtc { get; set; } = "02:00";

    /// <summary>Approved: 5,000 rows per batch, each batch its own short transaction.</summary>
    public int PurgeBatchSize { get; set; } = 5_000;

    /// <summary>Approved: health degrades when purgeable rows have been overdue for longer than this.</summary>
    public TimeSpan HealthLagThreshold { get; set; } = TimeSpan.FromHours(48);
}
