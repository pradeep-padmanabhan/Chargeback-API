# ADR-0006: PostgreSQL row-level security design

Status: **Proposed — required before shared multi-bank environments** · Date: 2026-09-29

## Context

Application-level scope checks are implemented and tested. CLAUDE.md requires database RLS as defence in depth before shared environments. The baseline does not enable RLS or create login roles.

## Proposal (not applied)

- **Dedicated roles:**
  - `chargeback_app`: DML only, no DDL, no `BYPASSRLS`.
  - `chargeback_migrator`.
  - `chargeback_readonly`.
- **Per-transaction settings:** the application sets `SET LOCAL app.user_id` and `app.bank_ids` (a uuid array built from the resolved scopes).
- **Policies on bank-owned tables:**
  - `disputes` by `bank_id`.
  - `cases`, `documents`, `document_slots`, `triage_results`, `portal_messages`, `zendesk_tickets`, `mastercom_filings` and `gate_results` through their dispute or case.
  - Denormalizing `bank_id` onto the child tables would make these policies cheaper, but that is a schema change.
- **Append-only enforcement by grants:** `domain_events`, `ai_decision_logs` and `filing_api_log` get no UPDATE or DELETE, except `domain_events.published_at`.
- **Workers** use an explicit system scope; no role bypasses RLS at runtime.

## Impact

Every query must run inside a transaction, or have its connection-level setting reset, so settings cannot leak across pooled connections. The approach needs a decision before Phase 5 data is shared.

## Note (2026-10-04)
RLS for the portal message thread was requested in migration 0009. It was **deferred to this ADR**, so that every bank-owned table is protected together under one design: database roles plus a per-transaction `app.bank_ids`. Until then, bank scope is enforced in the application and covered by integration tests. That includes bank users reaching only their own bank's thread, and another bank's case returning 404.
