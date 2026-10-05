namespace Chargeback.Api.Features.Triage.Contracts;

/// <summary>
/// The approved scheme calendar (SchemeRules:CalendarTimeZone, ADR-0122), exposed so other slices can compute
/// read-time values such as days remaining. <c>null</c> while the setting is not approved/configured.
/// </summary>
public interface ISchemeCalendar
{
    TimeZoneInfo? Calendar { get; }

    /// <summary>
    /// Whole calendar days from today (in the scheme calendar) until <paramref name="deadline"/>; negative when
    /// past. Null when no calendar is configured or there is no deadline. Whether the deadline day itself counts is
    /// still open (ADR-0122); this returns the plain date difference.
    /// </summary>
    int? DaysUntil(DateOnly? deadline, DateTimeOffset now);
}
