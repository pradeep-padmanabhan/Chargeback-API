# ADR-0122: Scheme date basis, filing clock and calendar

Status: **Proposed — business decision required** · Date: 2026-09-29

## Context
Selecting the effective rule version and calculating the filing deadline both depend on business choices that the approved documents leave open. Act 6 refers to "the approved trigger date", and ADR-0115 already records that the calendar basis is open. None of these are hard-coded. Each is a setting in the configuration section `SchemeRules`, and **none has a default**.

| Setting | Values supported | Used for | When missing |
|---|---|---|---|
| `CalendarTimeZone` | time-zone id, e.g. `UTC` | converting timestamps to dates | `ConfigurationPending`: no reason code is determined |
| `EffectiveDateBasis` | `TransactionDate`, `DisputeReceivedDate` | choosing the effective rule version | `ConfigurationPending` |
| `ClockStartBasis` | `TransactionDate`, `DisputeReceivedDate` | the filing-clock start date | reason code may be determined; deadline `ConfigurationPending` |
| `DeadlineDayCounting` | `CalendarDays` only | deadline = clock start + `time_limit_days` | deadline `ConfigurationPending` |

Other deadline outcomes: a rule with a NULL `time_limit_days` gets `NoTimeLimitDefined`, and a missing clock-start fact gets `MissingClockStartFact`.

Business-day counting is **not** implemented. It needs an approved holiday calendar per scheme and region.

`cases.days_remaining` is not written; see ADR-0115, which proposes computing it at read time.

## Decisions required
1. The effective-date basis.
2. The filing-clock trigger date. It may differ by reason code or stage, which would need a per-rule field.
3. The time zone(s).
4. Calendar vs. business days, and the calendar source if business days.
5. Whether "deadline day" is inclusive.
