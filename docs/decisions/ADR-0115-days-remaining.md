# ADR-0115: `cases.days_remaining` staleness

Status: Proposed — read-time computation implemented; calendar still pending · Date: 2026-09-29

## Gap
`cases.days_remaining` is stored, but it goes stale every day. The frontend must display server-calculated deadlines.

## Proposal
Compute `daysRemaining` at read time from `filing_deadline_date` and the approved calendar/time-zone rule. Treat the column as unused, or drop it in the next baseline revision.

## Needed
- The calendar basis: calendar vs. business days, and time zone.
- Whether "remaining" counts the deadline day itself.

## Phase 7 implementation
- **Calculation:** `daysRemaining` is computed at read time in `GET /cases` and `GET /cases/{id}`, as `filing_deadline_date − today`. "Today" is taken in the scheme calendar time zone, and the result is negative when past.
- **When it is null:** while `SchemeRules:CalendarTimeZone` is not approved and configured (ADR-0122). That is today's production state.
- **The column:** `cases.days_remaining` is never written.
- **Still open:** whether the deadline day itself counts (inclusive or exclusive), and business-day counting.
