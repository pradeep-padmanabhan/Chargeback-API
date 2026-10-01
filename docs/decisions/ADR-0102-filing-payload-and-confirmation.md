# ADR-0102: Prepared filing payload and explicit human confirmation

Status: Proposed — schema gap, approval needed · Date: 2026-09-29

## Gap
Act 6 requires showing the exact payload to the analyst and requiring explicit human confirmation before filing. `mastercom_filings` has no column for:
- the prepared payload;
- a hash of the payload;
- the confirming user (`confirmed_by`);
- the confirmation time (`confirmed_at`).

`filing_api_log` records exchanges, not the reviewed draft. `idempotency_key` is NOT NULL, so a draft row needs its key at preparation time.

## Proposal
Add these columns to `mastercom_filings`:
- `prepared_payload jsonb`
- `payload_sha256 varchar(64)`
- `prepared_by`, `prepared_at`
- `confirmed_by`, `confirmed_at`

Confirmation must present the `payload_sha256` it reviewed, and submission is refused if the hash differs. The Idempotency-Key is generated when the draft is prepared.

## Until resolved
The prepare/confirm endpoints are 501 stubs. `ConfirmFilingCommand` is deliberately not transactional, because submission is an external call.
