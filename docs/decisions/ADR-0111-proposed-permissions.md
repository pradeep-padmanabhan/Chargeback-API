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
