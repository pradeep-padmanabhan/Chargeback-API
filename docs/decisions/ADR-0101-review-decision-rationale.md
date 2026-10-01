# ADR-0101: Human review decision rationale

Status: Proposed — schema gap, approval needed · Date: 2026-09-29

## Gap
Act 5 requires recording "reviewer, time, decision **and rationale**". `cases` has `human_review_verdict`, `human_reviewed_by` and `human_reviewed_at` only: no rationale column, and only the latest decision is kept (no history if a case is reviewed twice).

## Options
1. Add `cases.human_review_rationale text` (latest decision only).
2. Add a `case_review_decisions` table (case_id, decision, rationale, reviewer, decided_at), append-only. **Recommended.**
3. Store the rationale only in the `domain_events` payload (not queryable; mixes the outbox with the audit trail).

## Until resolved
`POST /cases/{caseId}/review/decision` accepts `rationale` in the contract but stays a 501 stub.
