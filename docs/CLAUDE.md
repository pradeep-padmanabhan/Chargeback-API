# Chargeback API — Backend Team Instructions

**Read first:** `docs/CHARGEBACK_FRONTEND_BACKEND_AI_COMMON.md` (the shared common guide) before any implementation. This file adds backend-specific scope only — it does not repeat what the common guide already covers. Where an ADR in `docs/decisions/` conflicts with the guide, the guide wins: update the ADR and say so in your report.

## Stack

- .NET 9, Carter Minimal API, MediatR v12.x, FluentValidation, EF Core + Dapper
- PostgreSQL 16+, schema `chargeback_diagram`
- AWS: ECS Fargate, S3 (evidence), SQS FIFO, SNS, Bedrock (via BedrockAiClient)
- xUnit, Testcontainers (PostgreSQL). Each suite builds fresh databases from 0001 + all migrations; no Respawn.

## Hard dependency rules — do not upgrade without explicit approval

| Package | Constraint | Reason |
|---|---|---|
| MediatR | v12.x only | v13+ is commercially licensed |
| AutoMapper | **Do not use** | Commercial license |
| FluentAssertions | v7.x only | v8+ is commercially licensed |
| Shouldly | Allowed as alternative to FluentAssertions | |

## Approved MediatR pipeline order

```
Logging → Validation → Authorization → RlsSetup → Idempotency → Transaction → Handler
```

`PipelineOrderTests` enforce this order. `RlsSetupBehavior` (ADR-0006) establishes the request's database scope after authorization has resolved the caller:
- the caller's banks (`ICurrentUser.BankScopes`), for bank-scoped, resource-scoped and scope-filtered requests;
- system scope, for `[SystemOperation]` workflow steps;
- nothing, for `[NotBankScoped]` requests (fail closed).

`IDatabaseScope` writes the scope as session settings (`app.scope`, `app.bank_ids` as a `uuid[]`) on **every connection the request opens**: an EF connection interceptor, and Dapper right after it opens. It is reapplied immediately if the scope changes on an open connection.

Do **not** use `SET LOCAL` inside `TransactionBehavior`. Queries and non-transactional commands open no transaction, and one `app.bank_id` cannot serve a processor scoped to several banks.

Authorization's resource lookup (`ResourceBankResolver`) runs under `ElevateToSystemAsync`, because RLS would otherwise hide the row it must locate.

## Feature slices — current status

| Slice | Status | Migration |
|---|---|---|
| Intake | Scaffolded; gate registry built; blocked on ADR-0119–0122 for real config | 0001 |
| Triage & Rules | Engine built; **blocked on ADR-0119–0122** — do not invent thresholds or rule defaults | 0001 |
| Case Management | ✓ Shipped (incl. FLAG/UNFLAG, v1.9) | 0003 |
| Human Review | ✓ Shipped | 0006 |
| Evidence & Documents | ✓ Shipped | 0007 |
| Network Filing — Mastercom | 501 stubs marked `KNOWN_LIMITATION_MASTERCOM_` — blocked on external contracts | — |
| Client Portal & Communications | ✓ Shipped | 0009 |
| Admin & Configuration | ✓ Shipped | 0008 |
| Row-level security | ✓ Shipped | 0010 |

## Migrations — approved and shipped (0001–0010)

All scripts live in `db/migrations/` (0001 is `docs/CHARGEBACK_DIAGRAM_BASELINE.sql`). All are idempotent and transactional, and the application never runs them. **None applied to a shared environment yet.**

| # | File | Key contents |
|---|---|---|
| 0001 | baseline | Full diagram schema; `user_bank_scopes`; permission definitions only |
| 0002 | `add_create_dispute_permission` | `CREATE_DISPUTE` — defined, **not assigned to any role** |
| 0003 | `case_management` | `processed_domain_events`; `ASSIGN_CASE`; case status CHECK; `case_reference_seq` |
| 0004 | `idempotency_keys` | `idempotency_keys` with `resource_id` uuid and `completed_at timestamptz` |
| 0005 | `role_permission_matrix` | 4 PROCESSOR/ADMIN roles; 20 role_permission rows; `VIEW_TRIAGE`, `RETRIAGE_CASE` |
| 0006 | `human_review` | `case_review_decisions` (append-only trigger); `REVIEW_CASE`; 4 role grants |
| 0007 | `evidence_documents` | `documents` upload status / soft delete + immutability trigger; `document_classifications`; `UPLOAD_DOCUMENT`, `VIEW_DOCUMENTS`; 8 grants |
| 0008 | `admin_user_management` | `MANAGE_BANK_USERS` (**Admin only**); `Bank User` role (BANK type, no permissions); `users.invited_at/deleted_at/deleted_by` |
| 0009 | `portal_messages` | Baseline `portal_messages`: 1–2000 char CHECK, index, append-only trigger |
| 0010 | `row_level_security` | RLS enabled + forced on 12 bank-owned tables; policies over `app.scope` / `app.bank_ids`; `chargeback_app` / `chargeback_migrations` roles |

New structural changes go in a new numbered migration. The baseline only receives new permission *definitions*.

## Row-level security (migration 0010) — reference

**Protected tables** (one `bank_scope` policy each, `FORCE ROW LEVEL SECURITY`):

| Table | Bank reached through |
|---|---|
| `disputes` | `bank_id` |
| `cases`, `gate_results` | `dispute_id → disputes.bank_id` |
| `triage_results`, `document_slots`, `documents`, `case_review_decisions`, `portal_messages`, `zendesk_tickets`, `mastercom_filings` | `case_id → cases → disputes` |
| `document_classifications` | `document_id → documents → case` |
| `filing_api_log` | `filing_id → mastercom_filings → case` |

A row is visible when `app.scope = 'system'`, or when its bank is in `app.bank_ids`. Unset settings mean no rows.

**Not protected:**
- `roles`, `permissions`, `role_permissions`, `users` (the sign-in lookup runs before any bank is known), `user_bank_scopes`, `banks`, `scheme_reason_codes`, `scheme_rule_specs`, `idempotency_keys`, `processed_domain_events`;
- `domain_events` and `ai_decision_logs`: deferred, because they are written outside the request scope (guide §8 #42).

**Roles:**
- `chargeback_app`: NOLOGIN, NOBYPASSRLS, with DML grants.
- `chargeback_migrations`: NOLOGIN, BYPASSRLS.

The API **must** connect as a non-superuser login that is a member of `chargeback_app`; superusers and BYPASSRLS roles skip RLS silently. `KNOWN_LIMITATION_RLS_PRODUCTION_GRANTS_`: logins, Secrets Manager passwords and the final grant tightening are created at deploy time.

**Tests:**
- `RowLevelSecurityTests`: database level, as a non-superuser `chargeback_app` login.
- `RowLevelSecurityApiTests`: the full API journey with RLS enforced.
- `RlsSetupBehaviorTests`: unit tests.

The ordinary test hosts connect as the container superuser, so RLS is bypassed there. Any new bank-owned table needs a policy in a new migration, plus coverage in both RLS test classes.

## Branch and PR

- **Branch:** `feat/p1-contract-alignment`, 11 commits and 843 tests as of v1.9. PR: `https://github.com/pradeep-padmanabhan/Chargeback-API/compare/main...feat/p1-contract-alignment`.
- **New work:** add commits on top while the PR is open; after it merges, branch from `main`.

## Security rules — absolute, non-negotiable

- `NULL bank_id` on a `users` row **never implies cross-bank access**. Explicit `user_bank_scopes` rows are required.
- Another bank's resource always returns `404` (existence not revealed), never `403`.
- PAN masked to last 4 everywhere. Full PAN never in logs, AI prompts, case fields, response bodies.
- AI agents never derive reason codes, never file claims, never change case state.
- Explicit human confirmation required before any Mastercom filing — no background worker may bypass this.
- `CREATE_DISPUTE` is not assigned to any role. Do not assign without product and security approval.
- `MANAGE_BANK_USERS` is Admin-only. No other role.
- `FILED → CLOSED` is Admin-only, manual. No auto-close.

## Blocked items — do not unblock without approval

| Item | Blocked on |
|---|---|
| Triage real config | ADR-0119–0122 (bank thresholds, rule format, dispute facts, date basis) |
| Mastercom gateway | External contracts + sandbox credentials (§8 #3) |
| `POST /intake/bulk/dry-run` | §8 #26 (dry-run storage design) |
| `CREATE_DISPUTE` role assignment | Product + security approval (§8 #21) |
| `SUBMIT_MASTERCOM` role assignment | Filing phase (§8 #43) |
| Cognito invite flow | §8 #34 (infrastructure/security decision) |
| S3 real config (bucket, KMS, IAM) | §8 #29 (infrastructure) |
| RLS for `domain_events` / `ai_decision_logs` | §8 #42 (bank column or dispatcher system scope) |

## Stub naming convention

Unresolved external dependencies are wrapped in named stubs (grep for the marker):

- `KNOWN_LIMITATION_MASTERCOM_` — Mastercom filing endpoints (`FilingModule`)
- `KNOWN_LIMITATION_S3_` — evidence storage (`KnownLimitationS3Service`)
- `KNOWN_LIMITATION_INVITE_EMAIL_` — bank user invitation email/token
- `KNOWN_LIMITATION_ZENDESK_` — Zendesk ticket creation (`KnownLimitationZendeskClient`)
- `KNOWN_LIMITATION_RLS_PRODUCTION_GRANTS_` — RLS logins/grant tightening (migration 0010)
- `KNOWN_LIMITATION_ADR0106_` — retired: idempotency is implemented and no such stubs remain

## Reporting format

After each task, report: endpoints or migrations built, commands/tests run, test count total, assumptions made, new blocked items or open questions.
