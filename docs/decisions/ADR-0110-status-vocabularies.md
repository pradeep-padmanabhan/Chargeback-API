# ADR-0110: Status vocabularies

Status: **Case statuses and transitions approved** (transitions superseded 2026-10-01, see below); other vocabularies still open · Date: 2026-09-29

## Gap
These columns are free text with no CHECK constraint:
- `disputes.status`: default 'NEW'; the diagram also uses 'FLAGGED';
- `cases.status`;
- `cases.priority`;
- `mastercom_filings.filing_status` and `current_stage`;
- `banks.status`;
- `zendesk_tickets.status`.

The contract exposes them as strings. Transition rules are unknown; the guide says lifecycle branches are illustrative, not a straight line.

## Needed
- The approved value sets for each column.
- The allowed transitions, and who or what may trigger each.
- Which values are shown to banks (the curated portal view).

## Decision (Phase 7, 2026-09-29)
- **Case status values (approved):** `NEW`, `FLAGGED`, `UNDER_REVIEW`, `APPROVED`, `REJECTED`, `FILED`, `CLOSED`. Enforced by the `cases_status_check` CHECK constraint (migration 0003).
- **Allowed transitions** are enforced in the **application layer only** (`CaseStatusTransitions`), not in the database.
- **Initial status:** the case-creation workflow sets `NEW` (all ten gates passed) or `FLAGGED`.

### Transition table — PROPOSED, derived from the approved process flow; please confirm

| From | To | Who | Process source | Phase |
|---|---|---|---|---|
| NEW | UNDER_REVIEW | Analyst (`PATCH /status`) | Act 5: analyst takes the case into review | 7 ✅ |
| FLAGGED | UNDER_REVIEW | Analyst (`PATCH /status`) | Act 2/5: flagged case picked up from the queue | 7 ✅ |
| UNDER_REVIEW | APPROVED | Human Review decision (with rationale) | Act 5 | 9 |
| UNDER_REVIEW | REJECTED | Human Review decision (with rationale) | Act 5 | 9 |
| APPROVED | FILED | Mandatory human filing confirmation | Act 6 | 10 |
| REJECTED | CLOSED | Analyst (`PATCH /status`) | Act 5: rejection path completed | 7 ✅ |
| FILED | CLOSED | Recorded terminal scheme outcome | Act 7 | 10 |

- **Analyst changes through `PATCH /cases/{id}/status`:**
  - require `UPDATE_CASE_STATUS`, a reason, and `If-Match`;
  - write `case.status.changed` **before** the status update, in the same transaction (ADR-0109).
- **Not available through `PATCH /status`:** APPROVED, REJECTED and FILED. They are reserved for the audited Human Review and filing workflows. Any transition not listed is refused with `409 INVALID_STATUS_TRANSITION`.
- **Questions:**
  - Should NEW, FLAGGED or UNDER_REVIEW be closable directly, for example an invalid dispute or a withdrawal? That is currently not allowed.
  - Can UNDER_REVIEW return to FLAGGED or NEW?
  - Does triage outcome `Invalid` or `AutoRefund` lead to a status?
  - Is REJECTED → CLOSED by an analyst correct, or should it follow client notification automatically?

## Assignment (Phase 7 design choice, to confirm)
`PATCH /cases/{id}/assignment` requires `ASSIGN_CASE` and `If-Match`. The assignee must be able to work the case:
- an ACTIVE processor or admin user;
- whose active role holds `VIEW_CASES`;
- with a currently valid scope for the case's bank.

Otherwise the request fails with `409 ASSIGNEE_NOT_ELIGIBLE`. Each change records `case.assigned`.

## Still open
The vocabularies for `disputes.status` (beyond NEW and FLAGGED), `cases.priority`, `mastercom_filings.*`, `banks.status` and `zendesk_tickets.status`.

## Phase 7 corrections confirmed (2026-09-29)
- **Case creation is automatic for every dispute.** There is no deferred or manual creation. A FLAGGED dispute gets a FLAGGED case; a NEW dispute gets a NEW case, which is then triaged automatically. The case is created by the `case-creation` workflow step as soon as the dispute's gates are evaluated, and automatic triage follows for NEW cases.
- **Case status vocabulary:** `NEW`, `FLAGGED`, `UNDER_REVIEW`, `APPROVED`, `REJECTED`, `FILED`, `CLOSED`. Approved and enforced by `cases_status_check` (migration 0003).
- **Contracts:** the pagination, error and case-reference contracts are formally approved and published in `docs/contracts/openapi-v1.json`:
  - `ProblemDetails.code` and `correlationId`;
  - bounds for `page` and `pageSize`;
  - `caseReference` pattern `^CB-\d{4}-\d{6}$`.

## Superseded 2026-10-01: transitions endpoint (common guide v1.4 §3.2)
The transition table above is **approved**, with one change: `FILED → CLOSED` is an **admin-only manual** action, not a recorded scheme outcome. `PATCH /cases/{id}/status` has been **removed**.

- **Endpoint:** `POST /cases/{caseId}/transitions`, body `{action, rationale, expectedVersion}`, with an `Idempotency-Key` header (ADR-0106, atomic mode).
- **Actions:** `START_REVIEW` (NEW/FLAGGED → UNDER_REVIEW) and `CLOSE` (REJECTED → CLOSED; FILED → CLOSED for admins only). The handler picks the permission from the current status, so `CLOSE` is one action name.
- **Not here:** `APPROVE` and `REJECT` go through `/review/decision`, and `FILE` through `/filings/{id}/confirmation`. They appear in `validActions`, but `/transitions` refuses them with `422 INVALID_TRANSITION`.
- **`FLAG` / `UNFLAG`:** recognised names with no approved transitions; always `422`. The open questions below decide them.
- **Concurrency:** `expectedVersion` in the body; stale → `409 CASE_VERSION_MISMATCH`. No `If-Match`, 428 or 412 on this endpoint. `/assignment` keeps `If-Match`.
- **Rationale:** required for `CLOSE`, optional for `START_REVIEW` (recorded as `reason`, null if absent).
- **`validActions`** replaces `allowedAnalystTransitions` on the case detail. It is computed per caller: `UPDATE_CASE_STATUS` for `START_REVIEW`/`CLOSE`, admin user type for FILED → `CLOSE`, `REVIEW_CASE` for `APPROVE`/`REJECT`, `SUBMIT_MASTERCOM` for `FILE`. Bank users get none.
- **Still open:** what `FLAG` and `UNFLAG` move between, direct close of NEW/FLAGGED/UNDER_REVIEW, and the status effect of the `Invalid` and `AutoRefund` triage outcomes.

## Client Portal (2026-10-04)
The portal returns the **raw case status** for now. The bank-facing vocabulary, meaning which values banks see and how internal statuses map to them, is still open (guide §8 #44).

## Guide v1.8 (2026-10-04): FLAG and UNFLAG approved and implemented
- `FLAG`: NEW → FLAGGED.
- `UNFLAG`: FLAGGED → NEW.

Both go through `POST /cases/{id}/transitions` with `UPDATE_CASE_STATUS`, and the rationale is optional. They change only the case status: the dispute's status and gate results are unchanged, and no triage runs.

`validActions`: NEW → `[FLAG, START_REVIEW]`; FLAGGED → `[UNFLAG, START_REVIEW]`.
