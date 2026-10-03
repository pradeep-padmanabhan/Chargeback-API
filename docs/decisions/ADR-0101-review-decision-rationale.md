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

## Decision (2026-10-03): option 2, implemented
- **Table:** migration 0006 adds the append-only `case_review_decisions` table (`case_id`, `decision` APPROVED/REJECTED, `reason_code_id`, `rationale`, `reviewed_by`, `decided_at`).
  - A trigger refuses UPDATE and DELETE.
  - A CHECK requires a reason code on every APPROVED row.
  - `cases.human_review_verdict`, `human_reviewed_by` and `human_reviewed_at` mirror the latest decision.
- **Endpoint:** `POST /cases/{caseId}/review/decision` with body `{decision, rationale, reasonCodeId, expectedVersion}`; `Idempotency-Key` required; permission `REVIEW_CASE`.
  - The case must be UNDER_REVIEW (otherwise 422 `INVALID_TRANSITION`).
  - **Approve** sends back the case's deterministic derived reason code as confirmation:
    - 422 `REASON_CODE_NOT_DERIVED` when the case has none;
    - 422 `REASON_CODE_MISMATCH` when the id differs.
  - **Reject** carries no reason code.
  - A stale version returns 409 `CASE_VERSION_MISMATCH`.
- **One transaction:** the decision row, the timeline events `case.review.decided` and `case.status.changed` (action APPROVE/REJECT), and the status change. Timeline first (ADR-0109).
- **Rejection → client notification:** follows from `case.review.decided` once Phase 11 exists.
