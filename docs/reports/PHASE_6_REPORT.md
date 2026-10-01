# Phase 5 decisions and Phase 6 delivery report

**Date:** 2026-09-29
**Status:** implemented and tested locally. **Not committed or pushed**, awaiting review.
**Scope:** Phase 5 decisions (ADR-0106, 0111, 0116, 0117, 0118) and Phase 6 (the scheme rules engine and issuer triage engine).

> **Synthetic data notice.** No approved scheme rules, gate criteria, scheme date settings or bank triage configuration exist yet. Every definitive result in this report and in the tests comes from **clearly marked synthetic fixtures**: `SYN-*` codes, "SYNTHETIC" descriptions, and `Synthetic*` classes. They prove the mechanics only. **They do not validate any real scheme or bank business rule.**

---

## 1. Implementation summary

### Phase 5 decisions applied
| Decision | Implementation |
|---|---|
| **ADR-0117** (approved) | All 10 gates run in order and continue after a failure. Every result is recorded with an explicit **execution status**:<br>• `Passed` and `Failed` are decisions.<br>• `PendingDefinition` means the criteria are not approved, **or** a registered evaluator is not activated in `Intake:Gates:ActiveGates`.<br>• `TechnicalError` (reason `GATE_ERROR`) covers an exception, an explicit technical error, or an evaluator returning an inconsistent outcome.<br>The status appears in `GET /disputes/{id}/gates` as `executionStatus` and in the `dispute.gates.evaluated` event. NEW only if all 10 pass; an undecided or errored gate never counts as a pass. No gate criteria are implemented. |
| **ADR-0106** | No idempotency store. The duplicate-on-retry behaviour is documented as a **pre-production blocker**:<br>• in the ADR;<br>• in the OpenAPI description (clients **must not** auto-retry);<br>• in `api-conventions.md`;<br>• in the test `KNOWN_LIMITATION_ADR0106_…`. |
| **ADR-0111** | `CREATE_DISPUTE` has been:<br>• added to the baseline SQL, for fresh installs;<br>• written as the idempotent migration `db/migrations/0002_…` for existing development databases (not executed);<br>• assigned to **no** role. |
| **ADR-0116 / ADR-0118** | Format validation is unchanged. Gates 4 and 6 remain `PENDING_DEFINITION`. |

### Phase 6
**Scheme Rules Engine** (`Features/Triage/SchemeRules`)
- Reads every version of `scheme_rule_specs` joined with `scheme_reason_codes`.
- **Candidates:** only rules with `approval_status = 'APPROVED'` whose rule and reason code are both effective on the evaluation date. Bounds are inclusive.
- **Exclusions are counted for audit:** draft, retired, other status, not yet effective, expired, and reason code not effective.
- **Matching:** deterministic three-valued matching (true / false / unknown) over a closed catalogue of typed facts (ADR-0120, ADR-0121).
- **Determined** only when exactly one candidate matches and no candidate is unknown or invalid. Otherwise one of: `NoApprovedRules`, `NoMatch`, `Ambiguous`, `IncompleteFacts`, `InvalidRuleData`, `ConfigurationPending` or `Unavailable`.
- **Deadline:** clock start + `time_limit_days` in calendar days, using the configured basis. Deadline statuses: `Calculated`, `NoTimeLimitDefined`, `ConfigurationPending`, `MissingClockStartFact`.
- **Required documents:** parsed from `required_docs`, format proposed in ADR-0120.
- **Configuration:** every setting in the `SchemeRules` section (ADR-0122) is unset by default, so production returns `ConfigurationPending`.

**Issuer Triage Engine** (`Features/Triage/IssuerTriage`)
- **Components:** Hard Eligibility, Risk Scoring, Human Review Trigger and Routing Policy, driven by `BankTriageConfiguration` through `IBankTriageConfigurationProvider`.
- **Order:** the proposed evaluation order in ADR-0119.
- **Outcomes:** all six are reachable, **only through configuration**. Missing configuration, invalid configuration, unknown facts or an unmatched routing rule produce **Incomplete**: `outcome = NULL`, `human_review_triggered = true`, reason `TRIAGE_INCOMPLETE: …`.
- **Production provider:** `PendingApprovalBankTriageConfigurationProvider` always reports "not configured". There is **no database storage**, per instruction 3.3.

**Orchestration** (`EvaluateCaseTriageCommand`)
- **Trigger:** a **system-only** workflow step (ADR-0123), unreachable from HTTP. It refuses any dispute that isn't `NEW`, so FLAGGED disputes never progress.
- **Order:** it runs both layers first, then persists in **one atomic SaveChanges**:
  - the `triage_results` row;
  - `cases.derived_reason_code`, `clock_start_date` and `filing_deadline_date`, set only when determined and cleared otherwise;
  - the `triage.completed` audit event.
- **Safe fallback:** if rules or configuration are unreadable, the command returns `503 TRIAGE_UNAVAILABLE` and nothing is written. A concurrent case change returns `409` and nothing is written.
- **Recommendations only:** `AutoRefund` and `ProceedToFiling` are recorded. No refund, filing or case-status change is executed.

**Read API:** `GET /api/v1/cases/{caseId}/triage` requires `VIEW_TRIAGE` (proposed, not seeded), processor or admin user type, and bank scope.

---

## 2. Files

### Created
| Area | File |
|---|---|
| Security | `src/Chargeback.Api/Common/Security/SystemExecution.cs` |
| Triage | `src/Chargeback.Api/Features/Triage/Conditions/CaseFacts.cs`, `Conditions/Condition.cs` |
| | `src/Chargeback.Api/Features/Triage/SchemeRules/SchemeRuleRepository.cs`, `SchemeRulesOptions.cs`, `SchemeRulesEngine.cs` |
| | `src/Chargeback.Api/Features/Triage/IssuerTriage/BankTriageConfiguration.cs`, `IssuerTriageEngine.cs` |
| | `src/Chargeback.Api/Features/Triage/EvaluateCaseTriage/EvaluateCaseTriage.cs` |
| Database | `db/migrations/0002_add_create_dispute_permission.sql`, `db/migrations/README.md` |
| ADRs | `docs/decisions/ADR-0119` (bank configuration), `ADR-0120`, `ADR-0121`, `ADR-0122`, `ADR-0123` |
| Unit tests | `tests/Chargeback.UnitTests/Intake/GateStatusTests.cs`, `Behaviors/SystemOperationTests.cs`, `Triage/SyntheticFixtures.cs`, `Triage/ConditionTests.cs`, `Triage/SchemeRulesEngineTests.cs`, `Triage/IssuerTriageEngineTests.cs` |
| Integration tests | `tests/Chargeback.IntegrationTests/Infrastructure/BaselineDatabase.cs`, `Intake/Phase5DecisionTests.cs`, `Triage/TriageFixture.cs`, `Triage/TriageIntegrationTests.cs` |
| Report | `docs/reports/PHASE_6_REPORT.md` |

### Modified
| Area | File | Change |
|---|---|---|
| Schema | `docs/CHARGEBACK_DIAGRAM_BASELINE.sql` | `CREATE_DISPUTE` permission row (ADR-0111) |
| Kernel | `SharedKernel/Results/Error.cs`, `Result.cs` | `ErrorType.Unavailable` (503), `Result.Failure<T>` |
| API common | `Common/Results/Errors.cs`, `ResultHttpMapper.cs` | `SYSTEM_OPERATION_ONLY`, 503 mapping |
| | `Common/Security/Permissions.cs` | `CREATE_DISPUTE` moved to seeded |
| | `Common/Security/RequestAuthorization.cs`, `Common/Behaviors/AuthorizationBehavior.cs` | `[SystemOperation]` rule |
| | `ApiServiceRegistration.cs`, `appsettings.json` | Triage slice registration; empty `Intake:Gates` and `SchemeRules` sections |
| Intake | `Features/Intake/Gates/GateEngine.cs` | Execution status, activation config, consistency guard |
| | `Features/Intake/Contracts/IntakeContracts.cs` | `GateExecutionStatus`, `executionStatus` on `GateResultDto`, `DisputeStatuses` moved here |
| | `Features/Intake/SubmitDispute/SubmitDispute.cs`, `GetDispute/GetDispute.cs`, `IntakeModule.cs` | Event status lists, DTO status, no-retry description, options binding |
| Triage | `Features/Triage/Contracts/TriageContracts.cs`, `TriageModule.cs` | `evaluationStatus`; the real GET endpoint (was a stub) |
| Tests | `TestSupport/ChargebackApiFactory.cs` | `Settings` support |
| | `IntegrationTests.csproj`, `Infrastructure/PostgresFixture.cs` | Migration files copied to output; shared baseline helper |
| | `ArchitectureTests/ArchitectureRules.cs` | `SystemExecution` confinement; system operations not mapped to endpoints |
| Docs | `docs/contracts/openapi-v1.json` | Regenerated |
| | `docs/contracts/api-conventions.md`, `events.md`, `README.md`, `docs/decisions/README.md` | Updated for these changes |
| | ADR-0103, 0105, 0106, 0111, 0116, 0117, 0118 | Decisions and status updated |

The existing Phase 5 test files were **not modified** and all still pass.

---

## 3. Database migration and configuration changes
- **Fresh installs:** the baseline now seeds `CREATE_DISPUTE`. No other schema change was made; no tables or columns were added.
- **Existing development databases:** run `db/migrations/0002_add_create_dispute_permission.sql`. It is idempotent and assigns the permission to no role. `MigrationTests` proves it upgrades an original-baseline database to exactly the fresh-install permission set.
- **New configuration** (all empty, meaning nothing is decided until approved):
  - `Intake:Gates:ActiveGates: []`
  - `SchemeRules:{CalendarTimeZone, EffectiveDateBasis, ClockStartBasis, DeadlineDayCounting}: null`
- **Open decision:** migration tooling (Flyway, DbUp or other).

---

## 4. Test results (Release build, local; Docker Desktop 29.4.2)

| Suite | Result | Notes |
|---|---|---|
| Unit | **223 / 223** | +93 in this phase: conditions, both engines, gate statuses, system operations |
| Architecture | **186 / 186** | +2 rules: `SystemExecution` confinement; system operations not on endpoints |
| Contract | **6 / 6** | OpenAPI snapshot regenerated |
| Integration | **53 / 53** | +14; run three times consecutively, all green |

The format check and the vulnerable-package scan are clean, with 0 warnings and warnings treated as errors.

**Mutation checks:** after deliberately removing the "NEW only" guard in triage, the FLAGGED-dispute test failed as expected. The guard was restored.

**One flaky test was found and fixed:** a random GUID merchant token occasionally contained 13 or more consecutive digits and was correctly rejected as a possible card number. The token is now letters-only.

### Coverage (line coverage per area)

| Area | Unit | Integration |
|---|---|---|
| Intake gates | 97.9% | 83.3% |
| Pipeline behaviors | 91.3% | 96.3% |
| Scheme rules engine | 86.2% | 78.3% |
| Issuer triage engine | 93.7% | 67.1% |
| Conditions | 81.3% | 50.9% |
| Triage orchestration (`EvaluateCaseTriage`) | — | 97.3% |
| Intake submit/get | — | 100% |

Project totals from the integration run: Api 74.2% and Infrastructure 61.2% line coverage. Uncovered code is mostly the stub endpoints and the AWS/Bedrock adapters.

---

## 5. Integration demonstration (SYNTHETIC data)

Test: `TriageIntegrationTests.NEW_dispute_is_triaged_end_to_end_with_synthetic_rules_and_configuration`. Captured output:

```
DEMO dispute.status=NEW
DEMO gates=1:true,2:true,3:true,4:true,5:true,6:true,7:true,8:true,9:true,10:true   ← SyntheticPassingGate ×10
DEMO triage_result={"triageLayer":"ISSUER_TRIAGE","hardEligibilityPass":true,"routingPolicyOutcome":"SYN-ROUTE",
                    "riskScore":0.0000,"riskFlags":[],"humanReviewTriggered":false,"outcome":"ProceedToFiling",
                    "evaluationStatus":"Complete", …}
DEMO triage.completed={ "status":"Complete", "decidingLayer":"ISSUER_TRIAGE",
   "scheme":{"status":"Determined","reasonCode":"SYN-E2E-…","ruleSpecId":"f7d0…","evaluationDate":"2026-09-01",
             "clockStartDate":"2026-09-01","filingDeadlineDate":"2026-10-01","timeLimitDays":30,"deadlineStatus":"Calculated",
             "requiredDocuments":[{"slotName":"SYN cardholder letter","isRequired":true,"expectedType":"SYN-LETTER"}],
             "candidates":[…"NotMatched"…, …"Matched"…], "exclusions":{…all 0…}},
   "issuer":{"configurationVersion":"SYN-CONFIG-IT","components":[HardEligibility Evaluated, RiskScoring Evaluated,
             HumanReviewTrigger Evaluated, RoutingPolicy "rule 'SYN-ROUTE' matched"]} }
```

The same test also verifies:
- `cases.derived_reason_code` and `filing_deadline_date` are set;
- the case status is unchanged;
- **no `mastercom_filings` row** is created;
- an analyst with `VIEW_TRIAGE` and bank scope can read the result.

### Other integration paths demonstrated

| Path | Result |
|---|---|
| FLAGGED dispute (production gate configuration) | `409 DISPUTE_NOT_ELIGIBLE_FOR_TRIAGE`; nothing recorded |
| Production scheme configuration | Incomplete; `triage_layer = SCHEME_RULES`; reason `ConfigurationPending`; no reason code derived |
| No bank configuration | Scheme layer determined (derived code stored), issuer layer Incomplete; `outcome NULL`, `human_review_triggered = true` |
| Only a DRAFT rule matches | NoMatch → Incomplete |
| Rule expired the day before the transaction | Not used → Incomplete |
| AutoRefund outcome | Recorded only; no financial action; full audit event verified with no card data |
| Database failure reading rules | `503`; no triage row and no event |
| Bank configuration store unavailable | `503`; nothing recorded |
| User invoking triage directly | `403 SYSTEM_OPERATION_ONLY` |
| Reading triage results | Other bank's case `404`; no `VIEW_TRIAGE` `403`; bank user `403` |

---

## 6. Production readiness

| Component | State |
|---|---|
| Gate registry, engine, status handling, persistence | **Production-ready framework.** Zero gate criteria, so every dispute is FLAGGED. |
| Portal intake (validation, normalisation, atomic persistence, events) | Production-ready, **except the ADR-0106 duplicate-on-retry blocker** and `CREATE_DISPUTE` not yet assigned to any role. |
| Scheme rules engine | **Production-ready mechanics.** Real use needs approved rule data (ADR-0120 format, ADR-0121 facts) and the ADR-0122 settings. Today it returns `ConfigurationPending`. |
| Issuer triage engine | **Mechanics only.** The configuration model and storage are pending ADR-0119; the production provider is always "not configured". |
| Triage orchestration | Production-ready mechanics, but **no production trigger yet** (Phase 7 case creation). Invoked only from tests. |
| Triage read API | Implemented. `VIEW_TRIAGE` is not seeded (ADR-0111), so only tests can call it. |
| System execution context | Implemented; **security review requested** (ADR-0123). |
| Synthetic gate evaluators, rules and bank configuration | **Test-only**, under `tests/`. Not referenced by production code. |

---

## 7. Known limitations
1. **Duplicate disputes on retry (ADR-0106):** a pre-production blocker. Clients must not auto-retry.
2. **Every dispute is FLAGGED** until gate criteria are approved and evaluators activated.
3. **Every production triage is Incomplete** until ADR-0122 settings and ADR-0119 bank configuration are approved.
4. **Facts are too thin for real reason-code rules (ADR-0121):** there is no claim type, MCC, region, transaction type, etc.
5. **Audit trail:** the rule, configuration version and inputs are recorded only in the `triage.completed` event, because there are no columns (ADR-0103).
6. **Re-triage:** each run appends a result. The case's derived fields reflect only the latest run, and are cleared if it is not determined. The re-triage policy is not approved.
7. **`cases.days_remaining`** is not written; to be computed at read time (ADR-0115).
8. **Business-day deadline counting** is not supported (ADR-0122).
9. **Free-text card-number guard:** any value with 13–19 consecutive digits is rejected, which could affect legitimate merchant names or references containing long numbers.
10. **Time zones:** only `UTC` was exercised in tests. Named IANA zones depend on the host's time-zone data, and `InvariantGlobalization` is on.

---

## 8. Remaining business decisions and technical questions

| # | Decision / question | Owner | ADR |
|---|---|---|---|
| 1 | Bank triage configuration: storage, fields, evaluation order, approval workflow, per-bank values | Product owner, bank SMEs, security | **0119** |
| 2 | Rule condition and required-documents format; rule authoring and dry-run process; explicit rule key/version column | Scheme SMEs | 0120 |
| 3 | Additional dispute facts needed for rules, and where to capture and verify them | Scheme SMEs | 0121 |
| 4 | Effective-date basis, clock-start trigger, time zone(s), calendar vs. business days | Business | 0122 |
| 5 | Security sign-off on system execution; whether workers need a named service identity | Security | 0123 |
| 6 | Gate criteria for each of the 10 gates | Scheme SME | 0117 |
| 7 | Idempotency store design (pre-production blocker) | Architecture | 0106 |
| 8 | Role assignment for `CREATE_DISPUTE`; approval of `VIEW_TRIAGE` and the other proposed permissions | Product, security | 0111 |
| 9 | Triage audit columns (`rule_spec_id`, `bank_config_version`, `inputs_hash`) | Architecture | 0103 |
| 10 | Behaviour when no routing rule matches (currently Incomplete) | Product | 0119 |
| 11 | Re-triage policy; whether a non-determined run should clear previously derived case fields | Product | new |
| 12 | Migration tooling choice | Architecture | 0004 |

---

## 9. Proposed Phase 7 plan (Case Management) — for approval

**Goal:** create cases from intake, run triage automatically, and give analysts the case APIs, without changing the approved gate sequence or outcomes.

1. **Case-creation worker** (`Chargeback.Api.Workers`, the only namespace allowed to use `SystemExecution`):
   - Consumes `dispute.gates.evaluated` from the outbox.
   - **NEW:** create the case, then run `EvaluateCaseTriageCommand`.
   - **FLAGGED:** create the case for the analyst queue *if approved*, and never triage it.
   - Needs consumer de-duplication (ADR-0106 `processed_messages`). Until that is approved, the worker could rely on the `cases.dispute_id UNIQUE` constraint as a natural idempotency guard, which is safe for this one consumer.
2. **Case reference assignment:** needs an approved format (open; the guide places reference assignment in Act 6).
3. **Case APIs:** replace these stubs with implementations:
   - `GET /cases` (scope-filtered, paged);
   - `GET /cases/{id}`, with the derived reason code and deadline, and `daysRemaining` computed at read time per ADR-0115;
   - `GET /cases/{id}/timeline` (source per ADR-0109);
   - `PATCH /cases/{id}/status` (needs the ADR-0110 vocabulary and transitions);
   - `PATCH /cases/{id}/assignment` (needs `ASSIGN_CASE`).

   Add `ETag`/`If-Match` concurrency using `xmin`.
4. **Document checklist snapshot:** when a case's triage is determined, snapshot the required documents into `document_slots`, once and never silently rewritten (Act 4). Or defer this to Phase 8 if preferred.
5. **Tests:**
   - worker idempotency and replay;
   - FLAGGED never triaged;
   - scope isolation on all case endpoints;
   - status-transition rules;
   - concurrency conflicts;
   - end to end from intake through case and triage to the case API.

**Decisions needed before Phase 7:**
- case reference format;
- whether FLAGGED disputes get a case;
- status vocabulary and transitions (ADR-0110);
- timeline source (ADR-0109);
- consumer de-duplication (ADR-0106);
- re-triage policy;
- the `ASSIGN_CASE`, `VIEW_TRIAGE` and `UPDATE_CASE_STATUS` role matrix.

**Nothing has been committed or pushed. Waiting for review and approval before Phase 7.**
