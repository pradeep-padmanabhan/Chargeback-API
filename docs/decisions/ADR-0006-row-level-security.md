# ADR-0006: PostgreSQL row-level security

Status: **Accepted and implemented (migration 0010, 2026-10-04)** · Date: 2026-09-29, revised 2026-10-04

## Context
Bank scope was already enforced and tested in the application. Row-level security adds defence in depth: a missed `WHERE` clause cannot leak another bank's rows. It is required before any shared or staging environment.

## Decision
**Session scope.** The API sets two session settings on every connection it opens, and again whenever the request's scope changes:
- `app.scope`: `banks`, `system` or empty;
- `app.bank_ids`: a `uuid[]` literal holding *all* the caller's banks. A single `app.bank_id` could not serve a processor scoped to several banks.

The settings are written on every open, so a pooled connection never carries another request's scope. Unset settings mean no rows (fail closed).

**`RlsSetupBehavior`.** The pipeline is `Logging → Validation → Authorization → RlsSetup → Idempotency → Transaction → Handler`. The behavior sets:
- the caller's banks, for bank-scoped, resource-scoped and scope-filtered requests;
- system scope, for `[SystemOperation]` workflow steps;
- nothing, for `[NotBankScoped]` requests.

The scope is applied as each connection opens (an EF connection interceptor, and Dapper right after it opens its own connection), not with `SET LOCAL` inside a transaction. That is why it also covers queries and non-transactional commands, which open no transaction.

**Authorization's resource lookup** runs briefly under system scope (`IDatabaseScope.ElevateToSystemAsync`). It returns only the owning bank, and authorization then decides; without this the policy would hide the row and every resource would read as 404.

**Policies.** Each protected table has one `FOR ALL` policy, with the table `FORCE ROW LEVEL SECURITY`. A row is visible when `app.scope = 'system'`, or when its bank is in `app.bank_ids`:
- `disputes`: directly through `bank_id`;
- `cases` and `gate_results`: through their dispute;
- `triage_results`, `document_slots`, `documents`, `case_review_decisions`, `portal_messages`, `zendesk_tickets` and `mastercom_filings`: through their case;
- `document_classifications`: through its document;
- `filing_api_log`: through its filing.

Helper functions `rls_is_system`, `rls_bank_ids`, `rls_dispute_visible` and `rls_case_visible` are `SECURITY INVOKER`, so the lookups inside them are themselves filtered.

**Roles.** Migration 0010 creates two NOLOGIN group roles if missing:
- `chargeback_app` (NOBYPASSRLS), with DML grants and default privileges for future tables;
- `chargeback_migrations` (BYPASSRLS; skipped with a notice when the migrator is not a superuser).

The API must connect as a login that is a member of `chargeback_app`. Superusers and BYPASSRLS roles are not subject to RLS. `KNOWN_LIMITATION_RLS_PRODUCTION_GRANTS_`: the logins, their Secrets Manager passwords and the final grant tightening are created at deploy time.

## Not protected
- **No bank data, or needed before the scope is known:** `roles`, `permissions`, `role_permissions`, `users` (the sign-in lookup runs before any bank is known), `user_bank_scopes`, `banks`, `scheme_reason_codes`, `scheme_rule_specs`, `idempotency_keys`, `processed_domain_events`.
- **Deferred:**
  - `domain_events`: written by the outbox interceptor and read by the dispatcher on its own connection; user events have no case.
  - `ai_decision_logs`: written by the AI client on its own connection.

  Both need a bank column, or a dispatcher system scope, before they can be protected.

## Tests
- Database level, as a non-superuser login in `chargeback_app`, across all 12 tables:
  - an unset scope sees nothing;
  - a single bank sees only its own rows;
  - several banks see all of theirs;
  - system scope sees everything;
  - cross-bank `INSERT` returns 42501, and cross-bank `UPDATE` affects 0 rows;
  - `chargeback_migrations` bypasses RLS.
- The full API journey runs with RLS enforced: intake, case creation, case reads with another bank getting 404, transition, documents and classification, review decision, portal and messages.
- `RlsSetupBehavior` is unit-tested, including that every request that isn't `[NotBankScoped]` gets a scope.
- All existing tests pass unchanged, because RLS is additive.

## Corrections to the 2026-10-04 brief
- `cases` has no `bank_id`; the bank comes through `disputes`.
- `case_events`, `messages` and `gate_definitions` do not exist. The real tables are `domain_events` (deferred), `portal_messages` and `gate_results`.
- One `app.bank_id` became `app.bank_ids`.
- `SET LOCAL` inside the transaction became per-connection settings, because the order placed RlsSetup before Transaction and queries open no transaction.
- `CREATE ROLE IF NOT EXISTS` is not PostgreSQL syntax; guarded `DO` blocks are used instead.
- `users` is excluded, because of the sign-in lookup.
