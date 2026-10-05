using Chargeback.Api.Features.Triage.Conditions;
using Chargeback.Api.Features.Triage.Contracts;
using Microsoft.Extensions.Options;

namespace Chargeback.Api.Features.Triage.SchemeRules;

internal sealed class SchemeCalendar(IOptions<SchemeRulesOptions> options) : ISchemeCalendar
{
    public TimeZoneInfo? Calendar => options.Value.ResolveCalendar();

    public int? DaysUntil(DateOnly? deadline, DateTimeOffset now) =>
        deadline is { } date && Calendar is { } calendar
            ? date.DayNumber - CaseFacts.ToDate(now, calendar).DayNumber
            : null;
}
