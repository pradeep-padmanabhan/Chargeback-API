namespace Chargeback.Api.Features.Triage.SchemeRules;

public enum SchemeDateBasis
{
    /// <summary><c>disputes.transaction_date</c>.</summary>
    TransactionDate,

    /// <summary><c>disputes.created_at</c> (when the platform received the dispute).</summary>
    DisputeReceivedDate,
}

public enum DeadlineDayCounting
{
    /// <summary>time_limit_days are calendar days. Business-day counting needs an approved calendar and is not supported.</summary>
    CalendarDays,
}

/// <summary>
/// Configuration section <c>SchemeRules</c>. These are business decisions (ADR-0122); nothing has a default.
/// While a setting is missing the engine returns an explicit pending result instead of guessing.
/// </summary>
public sealed class SchemeRulesOptions
{
    public const string SectionName = "SchemeRules";

    /// <summary>Time-zone id used to turn timestamps into dates (effective dates, clock start). E.g. "UTC".</summary>
    public string? CalendarTimeZone { get; set; }

    /// <summary>Which date selects the effective rule version.</summary>
    public SchemeDateBasis? EffectiveDateBasis { get; set; }

    /// <summary>Which date starts the filing clock (the "approved trigger date", common guide Act 6).</summary>
    public SchemeDateBasis? ClockStartBasis { get; set; }

    public DeadlineDayCounting? DeadlineDayCounting { get; set; }

    internal TimeZoneInfo? ResolveCalendar()
    {
        if (string.IsNullOrWhiteSpace(CalendarTimeZone))
        {
            return null;
        }

        if (string.Equals(CalendarTimeZone, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(CalendarTimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}
