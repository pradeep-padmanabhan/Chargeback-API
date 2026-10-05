# ADR-0116: Amount precision vs. currency minor units

Status: Proposed — business precision rules pending (decision 2026-09-29) · Date: 2026-09-29

## Gap
`disputes.transaction_amount` is `numeric(18,2)`, but some currencies have 3 minor units (e.g. BHD, KWD, OMR, TND) and others 0 (e.g. JPY). `Money` currently rejects more than 2 decimal places rather than rounding silently.

## Options
1. Keep `numeric(18,2)` and reject 3-decimal currencies at intake (only valid if they are out of scope).
2. Change to `numeric(18,3)`, or store minor units as a bigint plus the currency's exponent. **Recommended if any 3-decimal currency is in scope.**

## Decision (2026-09-29)
- Request-format validation stays in place: at least 0, at most 2 decimal places, fits `numeric(18,2)`.
- Gate 4 (Amount/Currency Check) returns `PENDING_DEFINITION` until its business criteria are approved.
