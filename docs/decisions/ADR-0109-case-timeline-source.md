# ADR-0109: Case timeline source vs. outbox

Status: **Accepted (Phase 7 decision, 2026-09-29)** · Date: 2026-09-29

## Gap
Case Management needs an "append-only event history". The Client Portal shows "progress from the case event log", with no internal notes. The only candidate is `domain_events`, which is the outbox. It:
- has no actor column;
- has no visibility flag (internal vs. bank-visible);
- mixes integration events with audit history;
- can be pruned once events are published.

## Options
1. Use `domain_events` as the timeline too. Add `actor_id` and `visibility`, and never prune it.
2. Add a separate append-only `case_events` table, written in the same transaction; keep `domain_events` purely as the outbox. **Recommended.**

## Decision
- **Source:** `domain_events` filtered by `case_id` is the append-only case timeline. There is no separate timeline table.
- **API:** `GET /api/v1/cases/{caseId}/timeline`, oldest first. It requires `VIEW_CASES` and a processor or admin user type, because timeline data is internal.
- **Write order:** events are written **before** the case status changes, inside the same transaction. The status-change and assignment commands save the event row first, then the change, within one TransactionBehavior transaction.
- **Ordering:** timeline order is deterministic. The outbox stamps the events of one save with strictly increasing, microsecond-distinct `created_at` values, in the order they were raised. UUIDv7 ids alone are not monotonic within a millisecond; this was found and fixed in Phase 7.
- **Events on the timeline today:** `case.created`, `triage.completed`, `case.retriage.requested`, `case.status.changed`, `case.assigned`.
- **Not on the timeline:** dispute-level events (`dispute.*`), which carry no `case_id` because they precede the case. The `case.created` event links to the source event.
- **Bank-safe view:** the Client Portal will show a curated projection of the timeline (Phase 11). Bank users never read this raw timeline.
