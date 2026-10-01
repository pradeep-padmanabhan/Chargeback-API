# Architecture Decision Records

| ADR | Title | Status |
|---|---|---|
| [0001](ADR-0001-solution-structure.md) | Solution structure and vertical-slice conventions | Accepted |
| [0002](ADR-0002-package-pinning-and-licensing.md) | Package pinning and licensing | Accepted |
| [0003](ADR-0003-authorization-model.md) | Authorization model: DB permissions + explicit bank scope | Accepted |
| [0004](ADR-0004-schema-baseline-no-migrations.md) | Schema baseline, no migrations, ephemeral test databases | Accepted |
| [0005](ADR-0005-sdk-host-token-exchange.md) | SDK authentication via host-app token exchange | **Proposed — needs confirmation** |
| [0006](ADR-0006-row-level-security.md) | PostgreSQL row-level security design | **Proposed** |
| [0119](ADR-0119-bank-triage-configuration.md) | Bank-specific triage configuration | **Accepted as raised — implementation awaits individual approval** |
| [0120](ADR-0120-rule-condition-and-document-formats.md) | Rule condition and required-document JSON formats | **Accepted as raised — awaits individual approval** |
| [0121](ADR-0121-rule-fact-model-gaps.md) | Facts available for rule matching (schema gap) | **Accepted as raised — awaits individual approval** |
| [0122](ADR-0122-scheme-date-and-deadline-configuration.md) | Scheme date basis, filing clock and calendar | **Accepted as raised — awaits individual approval** |
| [0123](ADR-0123-system-execution-context.md) | System execution context for workflow steps | **Accepted as raised** |
| [0124](ADR-0124-manual-retriage.md) | Re-triage: manual, analyst-only | **Accepted** |
| [0125](ADR-0125-case-reference-sequence.md) | Case reference `CB-YYYY-NNNNNN` — global sequence, no reset, UTC year | **Accepted** |

## Schema gaps (Q7)

No tables or columns were added. Each gap needs approval before the phase that depends on it (all before Phase 5 per instruction).

| ADR | Gap | Blocks |
|---|---|---|
| [0101](ADR-0101-review-decision-rationale.md) | Human review decision rationale | Phase 9 |
| [0102](ADR-0102-filing-payload-and-confirmation.md) | Prepared filing payload and human confirmation | Phase 10 |
| [0103](ADR-0103-triage-rule-version-and-inputs.md) | Triage rule version and inputs | Phase 6 |
| [0104](ADR-0104-document-upload-stage-audit.md) | Immutable document upload-stage audit | Phase 8 |
| [0105](ADR-0105-bank-configuration-thresholds.md) | Bank configuration and triage thresholds — superseded by ADR-0119 | Phase 6 |
| [0106](ADR-0106-idempotency-and-consumer-dedup.md) | API idempotency (90-day TTL, nightly purge) and consumer de-dup — **accepted and implemented** | Done (confirmFiling: Phase 10) |
| [0107](ADR-0107-zendesk-replay-protection.md) | Zendesk webhook replay protection | Phase 11 |
| [0108](ADR-0108-ai-decision-log-scope.md) | AI decision log scope, versions and overrides | Phase 8/9 |
| [0109](ADR-0109-case-timeline-source.md) | Case timeline = `domain_events` by case — **accepted** | Done |
| [0110](ADR-0110-status-vocabularies.md) | Case statuses **approved**; transitions **proposed**; other vocabularies open | Phase 9/10 |
| [0111](ADR-0111-proposed-permissions.md) | Permissions — CREATE_DISPUTE, ASSIGN_CASE seeded; others proposed | Per feature |
| [0112](ADR-0112-bulk-intake-batches.md) | Bulk intake batch tracking | Phase 5 |
| [0113](ADR-0113-sdk-session-persistence.md) | SDK session persistence | SDK phase |
| [0114](ADR-0114-portal-notifications.md) | Client Portal notifications | Phase 11 |
| [0115](ADR-0115-days-remaining.md) | `daysRemaining` computed at read time; calendar pending | ADR-0122 |
| [0116](ADR-0116-amount-precision.md) | Amount precision vs. currency minor units | Phase 5 |
| [0117](ADR-0117-gate-result-versioning.md) | Gate behaviour (**approved**); result history and version column (proposed) | Re-evaluation |
| [0118](ADR-0118-intake-correlation-and-dedup-keys.md) | Intake correlation and duplicate-detection keys | Phase 5 |
