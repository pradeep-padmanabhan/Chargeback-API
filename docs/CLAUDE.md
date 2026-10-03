# Claude Code development instructions — .NET 9 API/backend (v2, aligned with common guide v1.5)

**Shared source of truth:** Read `CHARGEBACK_FRONTEND_BACKEND_AI_COMMON.md` (v1.5) **in full** before changing code. This file only defines your team's scope; do not create a separate architecture or override the common file. Track unresolved workflow decisions in your implementation report; do not invent answers. Where an ADR in `docs/decisions/` conflicts with the common guide, the guide wins; update the ADR and note it in your report.

**Schema selection:** This handoff uses the diagram-aligned MVP baseline in `CHARGEBACK_DIAGRAM_BASELINE.sql` (migration 0001) plus `db/migrations/0002`–`0005`, for a **new development database only**. Never mix it with the separately supplied expanded 110-feature package.

**Mandatory approach:** Start by inspecting the repository and existing implementation. Propose a short phased plan and file changes; implement only approved or unblocked tasks. Use the ten ER-diagram gates approved in the common guide; never fabricate their missing scheme thresholds, Mastercom endpoints or bank integration contracts. Keep all consequential chargeback decisions deterministic and human-controlled. Write tests with each feature. Report completed files, commands and tests run, assumptions and blocked questions.

## Your scope
Implement the single .NET 9 ECS-hosted application using Carter endpoints, MediatR behaviors, feature-owned vertical slices and a small Shared Kernel, per common guide §3 and §6.

## Standing rules
1. **Slices:** Intake, Triage & Rules, Case Management, Human Review, Evidence & Documents, Network Filing, Client Portal & Comms, Admin & Configuration. These are modules, not microservices.
2. **Pipeline order (approved):** `Logging → Validation → Authorization → Idempotency → Transaction → Handler`. The transaction applies **only to DB commands**; never wrap external network calls (Bedrock, S3, Textract, Mastercom, Zendesk) in a DB transaction.
3. **Licensing:** MediatR v12.x only (v13+ is commercial). No AutoMapper. FluentAssertions v7.x or Shouldly, never v8+.
4. **Shared Kernel** stays limited to genuinely common types: Result, Money, CurrencyCode, CardMasked, ReasonCode, current-user interface and event abstractions. Business logic stays in its slice.
5. **Schema changes:** migrations only (`db/migrations/NNNN_*.sql`): idempotent, transactional, never executed by the application (ADR-0004). The baseline may only receive new permission *definitions*; role assignments and structural changes go in migrations. Migrations 0001–0005 are approved (common guide §5).
6. **Authorization:** permissions come from `role_permissions` (approved matrix, guide §3.1, seeded by migration 0005). **NULL `bank_id` never implies cross-bank access**: processor users need explicit `user_bank_scopes` rows. Another bank's resource returns **404**; a filter on an unauthorized bank returns **403**. Add database RLS before shared multi-bank environments.
7. **Case status** changes only through `POST /cases/{caseId}/transitions` (guide §3.2): START_REVIEW, FLAG, UNFLAG, CLOSE. APPROVE/REJECT stay on `/review/decision` and FILE on `/filings/{id}/confirmation`. `validActions` is server-computed; invalid actions return `422 INVALID_TRANSITION`; concurrency uses `expectedVersion` in the body (409 when stale). No PATCH on status.
8. **Gates:** do not hardcode the diagram's conflicting gate list. Use the registry. Ungated or errored results are `passed = null` (`PENDING_DEFINITION` / `GATE_ERROR`) and the dispute stays FLAGGED (ADR-0117).
9. **Rules and triage:** deterministic, versioned scheme-rule matching, reason codes, deadlines and the six triage outcomes. AI may explain, never decide. Re-triage is manual only and requires `RETRIAGE_CASE` (guide §3.1, supersedes ADR-0124's permission).
10. **Documents:** case document slots, immutable upload-stage recording, independent processing status, S3 presigned upload (no streaming through the API server), asynchronous OCR and classification.
11. **AI:** typed capability interfaces through the common `BedrockAiClient`. Review summaries are generated once and persisted. Human decisions are audited. AI agents never derive reason codes, file claims or change case state. No PAN/CVV in prompts.
12. **Mastercom:** gateway behind a simulator, with explicit human confirmation before any filing. Outbound submission, query-before-retry and 16-queue polling depend on approved external contracts. Stub filing endpoints with `KNOWN_LIMITATION_MASTERCOM_` until the contracts are signed.
13. **Tests in CI:** xUnit unit tests, PostgreSQL/API integration tests (ephemeral DB = 0001 + all migrations), RLS/bank-scope tests (scoped-analyst persona, 14+ tests), messaging tests and external contract tests.

## Next tasks: common guide §9 alignment backlog
Do these before starting new phases. Each needs tests, and OpenAPI and `docs/contracts/` updates.
1. ~~**Permissions constants:**~~ **Done.** `RetriageCase` added; `ViewTriage` and `RetriageCase` are in `Permissions.Seeded`, and the full suite is green. Still to do: a `Migration_0005` idempotency and matrix test (20 rows, no bank scopes), and updates to ADR-0111.
2. ~~**Transitions:**~~ **Done.** `POST /cases/{id}/transitions` with `validActions`, `422 INVALID_TRANSITION` and `expectedVersion` (409 when stale); `PATCH /status` removed; ADR-0110 updated. `FLAG`/`UNFLAG` stay refused until their transitions are approved.
3. ~~**Gate results route:**~~ **Done.** `GET /disputes/{disputeId}/gate-results`; `/gates` removed with no alias.
4. ~~**Errors:**~~ **Done.** ProblemDetails carries `traceId`: the W3C trace id (`Activity.Current?.Id`), falling back to `HttpContext.TraceIdentifier`. `correlationId` is removed from error bodies; the `X-Correlation-Id` header and the outbox `correlationId` are unchanged.
5. ~~**Re-triage guard:**~~ **Done.** `POST /cases/{id}/retriage` requires `RETRIAGE_CASE`; ADR-0124 updated.
6. ~~**Pagination:**~~ **Done.** Default `pageSize` 20; `sortBy`/`sortDirection` on every list endpoint (`ISortableRequest` + `SortMap`, checked in `ValidationBehavior`); `400 INVALID_SORT_FIELD`; default `createdAt desc`. Open: clamp vs 400 for out-of-range `page`/`pageSize`.
7. **CORS:** expose `Retry-After`, `Idempotent-Replayed` and `ETag`.
8. **Bulk dry-run:** `POST /intake/bulk/dry-run` returns a `dryRunId` for `POST /intake/bulk`. This is blocked on the storage and retention decision (guide §8 #26); stub it and report.

## Idempotency key store (ADR-0106): implemented
- Migration 0004 adds the `idempotency_keys` table. `IdempotencyBehavior` sits between Authorization and Transaction.
- Error codes: `IDEMPOTENCY_KEY_REUSED` → 422; `IDEMPOTENCY_REQUEST_IN_PROGRESS` → 409 + `Retry-After: 5`; `DLQ_MESSAGE_EXPIRED` → 422.
- A replay sends `Idempotent-Replayed: true`. Failed attempts are not stored. Keys and `processed_domain_events` are purged after 90 days by the nightly job (advisory lock, batches of 5,000). Health reports `degraded` after 3 consecutive failed nights.
- Invert any remaining `KNOWN_LIMITATION_ADR0106_` stub once 3 consecutive integration runs pass with 0004 applied.

## Coordination contract
Publish and version OpenAPI (`docs/contracts/openapi-v1.json`) and event contracts for the frontend and AI teams. Do not invent table names beyond the selected schema and approved migrations. Use camelCase JSON, UTC ISO-8601 timestamps, `YYYY-MM-DD` deadline dates, RFC 7807 errors with a machine-readable `code`, and `CB-YYYY-NNNNNN` case references. Mask PAN to the last 4 digits everywhere; never put full PAN/CVV in logs, AI prompts, persisted case fields or response bodies.

## Exit criteria
A tested vertical slice demonstrates endpoint → MediatR → handler → PostgreSQL with scoped API access, mocked AI and third-party adapters, and a passing CI. Report unresolved decisions (common guide §8).
