# ADR-0124: Re-triage policy — manual, analyst-only

Status: **Accepted (Phase 7 decision, 2026-09-29)** · Date: 2026-09-29

## Decision
- Triage runs **automatically only once**: for a NEW case whose dispute is NEW, triggered by the case-creation workflow (`case.created`). FLAGGED cases are never triaged automatically.
- **Re-triage is manual and analyst-only**, with these conditions:
  - it is an explicit action: `POST /api/v1/cases/{caseId}/retriage` with body `{ "reason": "…" }`;
  - it requires permission `UPDATE_CASE_STATUS`, a processor or admin user type, and bank scope for the case;
  - it is allowed only when the case status is **FLAGGED** or **UNDER_REVIEW**; otherwise `409 RETRIAGE_NOT_ALLOWED`;
  - the reason is required: at most 1,000 characters, and a card number is rejected (Luhn-checked).
- There is **no automatic re-triage**: no scheduler, and no trigger on a rule or configuration change.

## Behaviour (implemented)
- Written in one atomic SaveChanges, in this order:
  1. `case.retriage.requested` (requester, reason, case status);
  2. the new `triage_results` row and the `triage.completed` event, with `trigger = { type: Manual, requestedBy, reason }`.
- **The case status never changes.** The outcome remains a recommendation.
- **The case's derived reason code and deadline** reflect the latest evaluation, and are cleared if it is not determined. Earlier results remain in `triage_results` and on the timeline.
- **Unavailable dependencies:** `503 TRIAGE_UNAVAILABLE`, and nothing is recorded, including the request.
- **FLAGGED cases:** re-triage evaluates the scheme and issuer layers on the recorded facts. It does **not** re-run the ten gates and does not change the dispute's FLAGGED status. Re-running gates on corrected facts is a separate, unapproved capability.

## Open points
- Should re-triage on a FLAGGED case be allowed while its gates are still pending? It is allowed today, per the decision.
- Should a manual re-triage that is not determined clear previously derived case fields? It currently does, because the case reflects only the latest approved derivation.

## Superseded 2026-10-01: permission
Re-triage now requires **`RETRIAGE_CASE`** (common guide v1.5 §3.1; held by Senior Analyst and Admin per migration 0005) instead of `UPDATE_CASE_STATUS`. Everything else in this ADR is unchanged.
