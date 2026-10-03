# Database migrations

| # | File | Purpose | Status |
|---|---|---|---|
| 0001 | [`docs/CHARGEBACK_DIAGRAM_BASELINE.sql`](../../docs/CHARGEBACK_DIAGRAM_BASELINE.sql) | Baseline schema, including the approved permission definitions (`CREATE_DISPUTE`, `ASSIGN_CASE`, `VIEW_TRIAGE`, `RETRIAGE_CASE`, `REVIEW_CASE`, `VIEW_DOCUMENTS`, `MANAGE_BANK_USERS`). Assigns no permission to any role | Approved |
| 0002 | [`0002_add_create_dispute_permission.sql`](0002_add_create_dispute_permission.sql) | Adds `CREATE_DISPUTE` (ADR-0111). Not assigned to any role | Approved; not executed |
| 0003 | [`0003_case_management.sql`](0003_case_management.sql) | Phase 7, in four parts (below) | Approved; not executed |
| 0004 | [`0004_idempotency_keys.sql`](0004_idempotency_keys.sql) | ADR-0106: the `idempotency_keys` table, plus a `processed_at` index for the 90-day purge | Approved; not executed |
| 0005 | [`0005_role_permission_matrix.sql`](0005_role_permission_matrix.sql) | Guide v1.4 §3.1: `VIEW_TRIAGE` and `RETRIAGE_CASE` definitions; roles Analyst, Senior Analyst, Compliance Officer and Admin; the approved role → permission matrix (20 rows). No bank scope is granted | Approved; not executed |
| 0006 | [`0006_human_review.sql`](0006_human_review.sql) | `REVIEW_CASE` for all four roles; append-only `case_review_decisions` table (ADR-0101), with a trigger that refuses UPDATE and DELETE | Approved; not executed |
| 0007 | [`0007_evidence_documents.sql`](0007_evidence_documents.sql) | `UPLOAD_DOCUMENT` and `VIEW_DOCUMENTS` for all four roles. `documents` gains `upload_status`, `upload_confirmed_at`, `deleted_at` and `deleted_by`, plus a trigger: upload-stage fields are immutable, upload status only moves forward, rows are never deleted. New append-only `document_classifications` table | Approved; not executed |
| 0008 | [`0008_admin_user_management.sql`](0008_admin_user_management.sql) | `MANAGE_BANK_USERS` for all four roles. Permissionless BANK-type `Bank User` role. `users.invited_at`, `deleted_at`, `deleted_by`, plus a CHECK that a deleted user is DISABLED | Approved; not executed |
| 0009 | [`0009_portal_messages.sql`](0009_portal_messages.sql) | The baseline `portal_messages` thread: 1–2,000 character CHECK, case index, append-only trigger. RLS deferred to ADR-0006 | Approved; not executed |

Migration 0003 adds:
- the `ASSIGN_CASE` permission, not assigned to any role;
- the `cases.status` CHECK constraint (ADR-0110);
- the `case_reference_seq` sequence (ADR-0125);
- the `processed_domain_events` table (ADR-0106).

## How to apply
- **Fresh database:** run 0001, then every numbered migration in order. Since Phase 7, structural changes live only in migrations, per instruction, so the baseline alone is no longer a complete install.
- **Existing development database:** run each migration it has not yet received, in order.
- Every script is idempotent and wrapped in a transaction. Run it with `psql -v ON_ERROR_STOP=1 -f <file>`.

## Rules
- The application never runs migrations or issues DDL (ADR-0004).
- Integration tests build every ephemeral database from 0001 plus all migrations, and prove each migration is idempotent (`MigrationTests`).
- **Open decision:** migration tooling and tracking (Flyway, DbUp, sqitch or other). Until then, record which scripts have been applied to each environment manually.
