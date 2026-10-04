# ADR-0111: Permissions beyond the seeded ten

Status: **CREATE_DISPUTE and ASSIGN_CASE approved and seeded**; remaining names still proposed · Date: 2026-09-29

## Gap
The baseline seeds 10 permissions. The API contract needs 11 more to avoid overloading unrelated ones. These are defined in `Permissions.cs` but **not seeded**, so every operation that requires one is denied to every user until approval.

| Proposed | Used by |
|---|---|
| ~~CREATE_DISPUTE~~ **approved and seeded 2026-09-29** | Intake (single and bulk) |
| VIEW_TRIAGE | Case triage results |
| REVIEW_CASE | Review queue, workspace, decision |
| ASSIGN_CASE | Case assignment |
| VIEW_FILINGS | Filing detail and API log |
| SEND_PORTAL_MESSAGE | Case message thread |
| MANAGE_BANKS | Create bank |
| MANAGE_ROLES | Roles and permission catalogue |
| MANAGE_BANK_SCOPES | Processor/admin bank-scope grants |
| VIEW_SCHEME_RULES | Reason codes and rule specs (read) |
| MANAGE_SCHEME_RULES | Create and approve rule versions |

Also needed: the role → permission matrix and the processor-to-bank scope matrix (guide §8.4). Integration tests insert these names into the ephemeral database only.

## Decision (2026-09-29)
- `CREATE_DISPUTE` is added to `CHARGEBACK_DIAGRAM_BASELINE.sql` for fresh installs.
- `db/migrations/0002_add_create_dispute_permission.sql` adds it to existing development databases. The migration is idempotent and not executed by the application.
- It is **not assigned to any role**. Assignment to a production role requires product and security approval.
- Tests prove it:
  - the baseline seeds exactly the documented set;
  - the migration upgrades an original-baseline database idempotently and assigns the permission to no role;
  - intake returns 403 without the permission.
- The other 10 proposed names, plus `MANAGE_BANK_TRIAGE_CONFIG` (proposed in ADR-0119), remain unapproved.

## Phase 7 decision (2026-09-29)
- **Requested:** add `VIEW_CASES`, `UPDATE_CASE_STATUS` and `ASSIGN_CASE` to the baseline permission definitions.
- **Only `ASSIGN_CASE` was new.** `VIEW_CASES` and `UPDATE_CASE_STATUS` were already in the original baseline and are unchanged.
- **Where `ASSIGN_CASE` is added:** the baseline (fresh installs) and migration 0003 (existing databases, idempotent).
- **Roles:** none of the three is assigned to any role. Role-permission seeding requires separate product and security approval.
- **Still proposed, not seeded:** `VIEW_TRIAGE`, `REVIEW_CASE`, `VIEW_FILINGS`, `SEND_PORTAL_MESSAGE`, `MANAGE_BANKS`, `MANAGE_ROLES`, `MANAGE_BANK_SCOPES`, `VIEW_SCHEME_RULES`, `MANAGE_SCHEME_RULES` and `MANAGE_BANK_TRIAGE_CONFIG` (ADR-0119).
- **Consequence:** `GET /cases/{id}/triage` (`VIEW_TRIAGE`) is still unusable outside tests.

## v1.5 decision (2026-10-01): approved matrix
- `VIEW_TRIAGE` and the new `RETRIAGE_CASE` are approved and defined in the baseline (fresh installs) and in migration 0005 (existing databases).
- **Migration 0005 seeds the approved role matrix** (common guide §3.1): Analyst, Senior Analyst, Compliance Officer and Admin, 20 `role_permissions` rows. It grants no bank scope.
- **Not in the matrix:** `CREATE_DISPUTE` (guide §8 #21). Its assignment is still open.
- **Still proposed, not seeded:** `REVIEW_CASE`, `VIEW_FILINGS`, `SEND_PORTAL_MESSAGE`, `MANAGE_BANKS`, `MANAGE_ROLES`, `MANAGE_BANK_SCOPES`, `VIEW_SCHEME_RULES`, `MANAGE_SCHEME_RULES` and `MANAGE_BANK_TRIAGE_CONFIG`.

## Human Review decision (2026-10-03)
`REVIEW_CASE` is approved:
- **Where it is defined:** the baseline (fresh installs) and migration 0006 (existing databases).
- **Who holds it:** all four roles (Analyst, Senior Analyst, Compliance Officer, Admin) via migration 0006.
- **What it guards:** the review queue, the workspace and the decision.

## Admin & Configuration decision (2026-10-03)
- **New permission:** `MANAGE_BANK_USERS` is approved. It is defined in the baseline and migration 0008, and held by all four roles. It guards inviting, updating and removing bank users.
- **Changed guard:** `GET /admin/banks` and `GET /admin/banks/{id}` now require `VIEW_BANK_USERS` instead of `VIEW_BANKS`, matching the approved matrix.
- **Now unused but still seeded:** `VIEW_BANKS`, `CREATE_BANK_USER`, `UPDATE_BANK_USER` and `DISABLE_BANK_USER`. No role holds them and no endpoint checks them. Whether to retire them is open (guide §8 #36).
- **New role:** migration 0008 adds the BANK-type `Bank User` role with no permissions. A user's role type must match the user type, and §3.1 says bank users hold no permissions.
- **Bank-admin role:** deferred pending product approval (§8 #33).

## Client Portal decision (2026-10-04)
- **Portal endpoints** carry no permission code. The gate is `userType = BANK` plus the user's own bank (`users.bank_id`).
- **Analyst message endpoints** (`GET/POST /cases/{id}/messages`) use `VIEW_CASES`, as briefed.
- **`SEND_PORTAL_MESSAGE`** remains proposed and unused (guide §8 #39).

## Guide v1.8 correction (2026-10-04)
`MANAGE_BANK_USERS` is **Admin only**. Migration 0008 now grants it to the Admin role alone (corrected before any environment applied it).
