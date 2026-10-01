# PAN false-positive fix and Phase 7 (Case Management) delivery report

**Date:** 2026-09-29
**Status:** implemented and tested locally. **Not committed or pushed**, awaiting review.
**Synthetic data:** gate evaluators, scheme rules and bank configuration used by tests are clearly labelled synthetic fixtures (`SYN-*`, `Synthetic*`). They prove the mechanics only, not business rules.

---

## 1. Bug fix — PAN false positive (completed before Phase 7 started)

| | Before | After |
|---|---|---|
| Free-text **rejection** (`PanRedactor.ContainsPan`, used by intake validation) | any 13–19 digit run | digit run **and** a Luhn pass |
| **Masking** of logs and AI input (`Redact` / `ContainsPanCandidate`) | any 13–19 digit run | unchanged, deliberately conservative |

### Regression tests

**Unit tests** (`PanLuhnRegressionTests`, 37 cases):
- **Accepted** (verified to fail Luhn): a 13-digit store reference, a 16-digit loyalty number, a 14-digit date-based reference, 16- and 19-digit member and order numbers, and a card-shaped grouped invoice number.
- **Rejected:** published test card numbers for Visa, Mastercard, Amex (15-digit), Discover, and dashed and grouped forms.

**Integration tests** (`Phase5DecisionTests`, 4 cases): real `POST /intake/disputes` calls. Two long references are accepted (202) and stored as entered; two test card numbers are rejected (400).

**Residual risk:** about 1 in 10 random digit strings passes Luhn by chance. A long reference that happens to be Luhn-valid is still rejected, and a mistyped card number (failing Luhn) is accepted in free text. It is still masked in logs and AI input.

---

## 2. Phase 7 implementation summary

### Workflow (event-driven, idempotent)
```
POST /intake/disputes ──► dispute + gate_results + dispute.gates.evaluated (outbox)
      │
      ▼  case-creation consumer (system-only; processed_domain_events)
   case NEW  (all 10 gates passed)  ─── case.created ──► automatic-triage consumer ──► triage_results + triage.completed
   case FLAGGED (otherwise)         ─── case.created ──► ignored: never triaged automatically (analyst queue)
```
- **Every dispute gets a case**, referenced `CB-{YYYY}-{NNNNNN}` from the database sequence `case_reference_seq`.
- **Consumers** record each consumed `eventId` in `processed_domain_events` **atomically with their effect**, check it before processing, and skip repeats silently with a log entry. A concurrent duplicate that loses the race hits the UNIQUE constraint and is also skipped.
- **Transport:** in-process (`Outbox:Transport = InProcess`, enabled in Development). SNS/SQS can replace it without changing the consumers.

### Case APIs (processor and admin users only)

| Endpoint | Permission | Behaviour |
|---|---|---|
| `GET /cases` | VIEW_CASES | Bank-scope filtered, paged; status filter validated; `daysRemaining` computed at read time (null until the calendar is approved) |
| `GET /cases/{id}` | VIEW_CASES | Derived reason code (deterministic rules only), deadline, `version` plus `ETag`, `allowedAnalystTransitions` |
| `GET /cases/{id}/timeline` | VIEW_CASES | `domain_events` for the case, oldest first (ADR-0109) |
| `PATCH /cases/{id}/status` | UPDATE_CASE_STATUS | Requires `If-Match` (428/412) and a reason. Analyst transitions only (see §6). Writes `case.status.changed` **before** the status update, in one transaction |
| `PATCH /cases/{id}/assignment` | ASSIGN_CASE | Requires `If-Match`. The assignee must be an active processor or admin with VIEW_CASES and current scope for the bank. Writes `case.assigned` first |
| `POST /cases/{id}/retriage` | UPDATE_CASE_STATUS | FLAGGED or UNDER_REVIEW only; reason required; writes `case.retriage.requested`, then the triage result, atomically; status unchanged (ADR-0124) |

### Also changed
- **Automatic triage** now also requires the case to be `NEW`, and records its source event for idempotency.
- **Triage runner:** triage logic moved into the shared `CaseTriageRunner`, used by both the automatic and manual paths. The audit event now carries `trigger`.
- **New error types:** `412 Precondition Failed` and `428 Precondition Required`, for `If-Match`.
- **Timeline ordering bug found and fixed:** events raised in one save had identical `created_at` values, and UUIDv7 ids are not monotonic within a millisecond, so their order was random. The outbox now stamps them strictly increasing, in raise order. Three consecutive integration runs were green after the fix.

---

## 3. Files

### Created
| File | Purpose |
|---|---|
| `db/migrations/0003_case_management.sql` | `ASSIGN_CASE`; `cases_status_check`; `case_reference_seq`; `processed_domain_events` |
| `src/Chargeback.Api/Features/Cases/CreateCase/CreateCaseForDispute.cs` | System-only case creation, reference generator, `CaseErrors`, `case.created` |
| `src/Chargeback.Api/Features/Cases/GetCases/CaseQueries.cs` | List, detail reader, timeline |
| `src/Chargeback.Api/Features/Cases/ChangeCase/ChangeCase.cs` | Status change, assignment, If-Match check, events |
| `src/Chargeback.Api/Features/Triage/EvaluateCaseTriage/CaseTriageRunner.cs` | Shared triage runner, `TriageTrigger`, audit events |
| `src/Chargeback.Api/Features/Triage/RetriageCase/RetriageCase.cs` | Manual re-triage (ADR-0124) |
| `src/Chargeback.Api/Features/Triage/Contracts/SchemeCalendar.cs`, `Triage/SchemeRules/SchemeCalendar.cs` | Read-time days remaining |
| `src/Chargeback.Api/Workers/WorkflowConsumers.cs` | `case-creation` and `automatic-triage` consumers |
| `src/Chargeback.Infrastructure/Outbox/InProcessDelivery.cs` | Consumer interface and in-process transport |
| `docs/decisions/ADR-0124-manual-retriage.md` | New ADR: re-triage policy |
| `docs/decisions/ADR-0125-case-reference-sequence.md` | New ADR: case reference generation |
| `docs/reports/PHASE_7_REPORT.md` | This report |
| `tests/Chargeback.UnitTests/Security/PanLuhnRegressionTests.cs`, `tests/Chargeback.UnitTests/Cases/CaseManagementUnitTests.cs` | Unit tests |
| `tests/Chargeback.IntegrationTests/Cases/CaseFixture.cs`, `Cases/CaseManagementTests.cs` | Integration tests |

### Modified
| File | Change |
|---|---|
| `docs/CHARGEBACK_DIAGRAM_BASELINE.sql` | `ASSIGN_CASE` definition |
| `src/Chargeback.SharedKernel/Security/PanRedactor.cs` | Luhn check; `ContainsPanCandidate` |
| `src/Chargeback.SharedKernel/Results/Error.cs` | 412/428 types |
| `src/Chargeback.Api/Common/Logging/PanRedactionEnricher.cs` | Masks candidates, not only Luhn-valid values |
| `src/Chargeback.Api/Common/Results/ResultHttpMapper.cs` | 412/428 mapping |
| `src/Chargeback.Api/Common/Security/Permissions.cs` | `ASSIGN_CASE` seeded |
| `src/Chargeback.Api/ApiServiceRegistration.cs` | Slice and consumer registration |
| `src/Chargeback.Api/appsettings.json`, `appsettings.Development.json` | Outbox transport |
| `src/Chargeback.Api/Features/Cases/Contracts/CaseContracts.cs` | Statuses, transition table, DTO version fields |
| `src/Chargeback.Api/Features/Cases/CasesModule.cs` | Stubs replaced with real endpoints |
| `src/Chargeback.Api/Features/Triage/EvaluateCaseTriage/EvaluateCaseTriage.cs` | Uses the runner; NEW-case guard; source event |
| `src/Chargeback.Api/Features/Triage/TriageModule.cs` | Re-triage endpoint and registrations |
| `src/Chargeback.Infrastructure/Persistence/Entities/IntegrationEntities.cs`, `ChargebackDbContext.cs`, `BaselineModel.cs` | `ProcessedDomainEvent` mapping |
| `src/Chargeback.Infrastructure/Persistence/Interceptors/OutboxInterceptor.cs` | Ordered, distinct timeline stamps |
| `src/Chargeback.Infrastructure/Outbox/OutboxDispatcher.cs`, `DependencyInjection.cs` | Transport option |
| `tests/Chargeback.IntegrationTests/Infrastructure/BaselineDatabase.cs`, `PostgresFixture.cs`, `Triage/TriageFixture.cs` | Databases built from baseline plus all migrations; the triage fixture is extensible |
| `tests/Chargeback.IntegrationTests/Persistence/SchemaConformanceTests.cs` | 22 tables, including `processed_domain_events` |
| `tests/Chargeback.IntegrationTests/Security/EndpointSecurityTests.cs` | Well-formed bodies and `If-Match`, so the bank-scope check is what is exercised |
| `tests/Chargeback.IntegrationTests/Intake/Phase5DecisionTests.cs` | PAN regression cases |
| `docs/contracts/openapi-v1.json`, `api-conventions.md`, `events.md` | Contract updates |
| `README.md`, `db/migrations/README.md` | Status and install instructions |
| `docs/decisions/README.md`; ADR-0004, 0106, 0109, 0110, 0111, 0115 | Decisions recorded |

---

## 4. Database migration and configuration changes
- **Migration 0003** is idempotent and not executed by the application. It adds:
  - the `ASSIGN_CASE` permission, assigned to **no** role;
  - the `cases_status_check` CHECK constraint (the 7 approved values);
  - `case_reference_seq`, with maximum 999999 and no cycle;
  - the `processed_domain_events` table, UNIQUE on `event_id`.

  `MigrationTests` proves it is idempotent against an original baseline database.
- **Baseline:** adds the `ASSIGN_CASE` definition only. `VIEW_CASES` and `UPDATE_CASE_STATUS` already existed and are unchanged.
- **Install procedure change:** a fresh database is now baseline **plus all migrations in order** (ADR-0004 amendment), because structural changes live only in migrations, as instructed.
- **Configuration:**
  - `Outbox:Transport` defaults to `Logging`; Development uses `InProcess` with the dispatcher enabled.
  - `SchemeRules:*`, `Intake:Gates:*` and bank configuration are unchanged and unapproved.

---

## 5. Test results (Release build, local)

| Suite | Result | New in this delivery |
|---|---|---|
| Unit | **298 / 298** | +37 PAN regression cases, +38 case-management cases |
| Architecture | **194 / 194** | +8, from new requests covered by the convention theories |
| Contract | **6 / 6** | OpenAPI snapshot regenerated |
| Integration | **80 / 80** | +4 PAN regression, +23 case management; three consecutive green runs |

The format check and vulnerable-package scan are clean, with 0 warnings.

**Mutation check:** disabling the analyst-transition guard made all 6 transition tests fail. The guard is restored.

### Coverage (integration run, line coverage)

| Area | Coverage |
|---|---|
| `Features/Cases` | 85–100% |
| Re-triage | 97% |
| Automatic triage and runner | 96% |
| Workers | 86% |
| Api total | 78% |

### Integration scenarios covered

**Workflow and data integrity**
- A FLAGGED dispute produces a FLAGGED case that appears in the queue and is not triaged.
- A NEW dispute produces a NEW case with automatic triage, whose timeline is `case.created`, then `triage.completed` (`trigger = Automatic`), and no filing.
- Redelivered events are skipped: one case, one triage, two processed records.
- References follow `CB-YYYY-NNNNNN` and increase with the sequence.
- The database rejects statuses outside the approved set.
- Migration 0003 is idempotent.

**Case APIs**
- Detail returns the ETag, derived code, deadline and days remaining. Days remaining are null on the production host.
- The list is scope-filtered, and an invalid status gives 400.
- A status change works with a reason and version, and the timeline shows from, to, reason and actor.
- All six non-analyst targets are refused with nothing recorded.
- Status-change failures:
  - no `If-Match` → 428;
  - stale `If-Match` → 412;
  - missing reason, invalid status, or a card number in the reason → 400.
- Permission and scope for status changes: 403 without permission, 404 for another bank, 403 for bank users.
- Assignment: eligible analysts succeed; ineligible assignees (no scope, no permission, a bank user, an unknown id) get 409; unassign works; `ASSIGN_CASE` is required.
- Re-triage:
  - a FLAGGED case works; the order is requested, then completed; the status is unchanged;
  - a NEW case gets 409; an empty reason gets 400; no permission gets 403; another bank gets 404;
  - an UNDER_REVIEW case is allowed.
- The timeline is refused to bank users.

---

## 6. Known limitations and design choices to confirm
1. **Transition table (ADR-0110) is PROPOSED.** It is derived from the approved process flow; the analyst transitions are NEW→UNDER_REVIEW, FLAGGED→UNDER_REVIEW and REJECTED→CLOSED. APPROVED, REJECTED and FILED are reserved for the audited Review and Filing workflows in Phases 9 and 10. No direct close from NEW, FLAGGED or UNDER_REVIEW. **Please confirm or correct.**
2. **Assignment eligibility:** active processor or admin, VIEW_CASES, and current bank scope. This is a security and consistency choice. Please confirm.
3. **Reference sequence (ADR-0125):**
   - global and non-resetting, with 999,999 cases in total;
   - may have gaps;
   - the year is taken in UTC.
4. **API idempotency (ADR-0106)** remains a **pre-production blocker**: retried intake duplicates disputes. Consumer idempotency is done.
5. **Poison events:** the dispatcher claims a batch in one transaction, so a permanently failing consumer blocks the rest of its batch. A dead-letter policy is needed before production.
6. **`processed_domain_events` is UNIQUE on `event_id` only,** as decided. A second consumer of the same event type would need `(consumer, event_id)`.
7. **Case creation is eventual:** the intake response returns before the case exists. The frontend polls or refreshes.
8. **Re-triage of a FLAGGED case** re-runs only the triage layers, not the gates. The dispute stays FLAGGED.
9. **Still blocked:**
   - `VIEW_TRIAGE` is not seeded, so the triage read API works only in tests;
   - production triage is always Incomplete (ADR-0119, ADR-0122);
   - every dispute is FLAGGED until gate criteria are approved.

## 7. Production readiness

| Component | State |
|---|---|
| Case creation workflow and consumer idempotency | Production-ready mechanics; needs an SNS/SQS transport and a DLQ policy |
| Case APIs, concurrency, timeline | Production-ready; the transition table awaits confirmation |
| Manual re-triage | Production-ready per ADR-0124 |
| Automatic triage | Mechanics ready; outcomes stay Incomplete until ADR-0119 and ADR-0122 are approved |
| Synthetic gates, rules and configuration | **Test-only** |

## 8. New questions
1. Confirm the case **transition table** (§6.1), including direct close and backward moves.
2. **ADR-0125:**
   - Does the reference number reset yearly?
   - Which time zone applies to `{YYYY}`?
   - Are gaps acceptable?
3. Confirm the **assignment eligibility** rule.
4. The **dead-letter policy** for failing consumers.
5. Should re-triage of a FLAGGED case be allowed while its gates are still pending (implemented as allowed, per decision)?
6. **Role-permission matrix** for `VIEW_CASES`, `UPDATE_CASE_STATUS` and `ASSIGN_CASE`, and approval of `VIEW_TRIAGE`.
7. Should dispute-level events (`dispute.*`) appear in the case timeline? They currently don't, because they have no `case_id`.

## 9. Proposed Phase 8 — Evidence & Documents (for approval)
1. **Document checklist snapshot:** take a snapshot from the determined triage's required documents onto `document_slots` when triage first determines them. It is created once and never silently rewritten after a rule change (Act 4). Decide what happens if a later re-triage determines different documents.
2. **Upload flow:**
   - `POST /cases/{id}/documents` returns a pre-signed S3 PUT (S3 adapter behind an interface, simulator in tests).
   - `POST /documents/{id}/upload-completion`.
   - Stage (Initial, PreArbitration, Arbitration) is stored separately from processing status (Pending, Processing, Success, Failed).
3. **Asynchronous processing:** an outbox consumer handles Textract OCR and AI classification, both behind interfaces with simulators. Failures are recorded for retry and never lose the case. AI classification is stored as advisory only.
4. **Reprocessing:** `POST /documents/{id}/reprocessing` for failed documents.
5. **Portal access:** bank users upload to their own cases (UPLOAD_DOCUMENT plus scope).

**Decisions needed:**
- Q13: is the stage client-supplied or derived from the case?
- ADR-0104: immutable upload-stage audit.
- S3 bucket, KMS and retention settings.
- Allowed MIME types and size limits.
- Textract usage approval.
- ADR-0108: AI decision-log fields.

**Nothing has been committed or pushed. Awaiting review and approval before Phase 8.**
