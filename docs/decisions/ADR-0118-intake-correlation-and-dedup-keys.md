# ADR-0118: Intake correlation and duplicate-detection keys

Status: Proposed — duplicate criteria pending (decision 2026-09-29) · Date: 2026-09-29

## Gap
- Act 1 says to "persist intake channel, masked case facts and correlation/audit identifiers". `disputes` has no correlation id, external or bank reference, submitter (user or API client), or idempotency key.
- Gate 6 (Duplicate Check) needs a defined duplicate key. The candidate is the bank + ARN + masked card + amount + date, but the correct key is a business decision.

## Proposal
- Add to `disputes`: `correlation_id`, `submitted_by`, `bank_reference` and `dedup_key` (hash of the approved key fields).
- Add a partial unique index or a lookup index, as the business decides.

## Decision (2026-09-29)
- Duplicate-detection keys, matching criteria and time windows are pending approval.
- Gate 6 (Duplicate Check) returns `PENDING_DEFINITION` until then.
