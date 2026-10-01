# API conventions (v1)

The machine-readable contract is [`openapi-v1.json`](openapi-v1.json) (OpenAPI 3.0). A running API also serves it at `/openapi/v1.json`. A CI contract test fails whenever the served document differs from the committed file, so every contract change shows up in the pull request diff.

## Basics
- Base path `/api/v1`. JSON uses camelCase, and enums are serialized as strings.
- Authentication: `Authorization: Bearer <Cognito access token>`. There is one user pool, and only the access tokens of the Analyst and Client portal app clients are accepted.
- Everything else about the caller (user type, role, permissions, bank scope) comes from the database. `GET /api/v1/me` returns it for the frontend `authStore`.
- `X-Correlation-Id` (optional, 1–64 chars `[A-Za-z0-9._-]`) is echoed back on every response and appears in logs and, as `traceId`, in error bodies.

## Errors (RFC 9457 problem details)
Every failure is `application/problem+json`: `{ type, title, status, detail, code, errors?, traceId }`. `code` is stable and machine-readable; `traceId` equals the request's `X-Correlation-Id`. There is no `correlationId` field.

| Status | Typical `code` | Meaning |
|---|---|---|
| 400 | `VALIDATION_FAILED` (with `errors`), `IDEMPOTENCY_KEY_REQUIRED` | Malformed request |
| 401 | — | Missing or invalid token |
| 403 | `USER_NOT_PROVISIONED`, `USER_NOT_ACTIVE`, `USER_ROLE_MISMATCH`, `USER_TYPE_NOT_ALLOWED`, `PERMISSION_DENIED` | Authenticated but not allowed |
| 404 | `RESOURCE_NOT_FOUND` | Missing **or outside your bank scope** (deliberately indistinguishable) |
| 409 | operation-specific, e.g. `CASE_VERSION_MISMATCH` (on `/transitions`), `RETRIAGE_NOT_ALLOWED`, `ASSIGNEE_NOT_ELIGIBLE` | Business-state conflict |
| 422 | `INVALID_TRANSITION` | The action is not available for the case's current status, or is not performed through `/transitions` |
| 412 / 428 | `CASE_VERSION_MISMATCH` / `CASE_VERSION_REQUIRED` | `If-Match` stale or missing (`/assignment` only) |
| 503 | `TRIAGE_UNAVAILABLE` | A dependency is unavailable; nothing was recorded; retry later |
| 501 | `NOT_IMPLEMENTED` | Contract stub: you were authorized, but the feature is not built yet |

The UI can hide actions with `PermissionGuard`, but the backend re-checks every call.

## Paging
List endpoints accept `page` (1-based) and `pageSize` (1–100, default 25), and return `{ items, page, pageSize, totalCount, totalPages }`.

## Idempotency (ADR-0106, approved)
The following require an `Idempotency-Key` header (8–128 chars `[A-Za-z0-9._:-]`). A retry must reuse the same key. Keys are scoped to the calling user and the operation, and are kept for 90 days.
- `POST /intake/disputes`
- `POST /intake/bulk`
- `POST /filings/{filingId}/confirmation`

| Situation | Response |
|---|---|
| Same key and request | The stored response, plus `Idempotent-Replayed: true` |
| Same key, different request | `422 IDEMPOTENCY_KEY_REUSED` |
| First request still running | `409 IDEMPOTENCY_REQUEST_IN_PROGRESS` |
| Key older than 90 days | Treated as new |
| Request that failed (4xx/5xx) | Stores nothing, so the key can be retried |

`submitDispute` is implemented in atomic mode. `confirmFiling` (reservation mode) arrives in Phase 10 and is a stub today.

## Concurrency
State-changing case operations use optimistic concurrency, backed by PostgreSQL `xmin`. `GET /cases/{id}` returns `ETag: "<version>"` and the same value as `version`. `POST /cases/{id}/transitions` takes it in the body as `expectedVersion`: a missing value returns **400**, and a stale one returns **409** `CASE_VERSION_MISMATCH`. `If-Match` is not used there. `PATCH /cases/{id}/assignment` still requires it as `If-Match`: a missing header returns **428** `CASE_VERSION_REQUIRED`, and a stale or malformed one returns **412** `CASE_VERSION_MISMATCH`.

## Card data and AI
- Card numbers appear only masked to the last four digits (`************1234`). The API never accepts or returns a full PAN or CVV.
- AI-derived content is always marked advisory (`advisory: true`, or an `Advisory*` type). Reason codes, deadlines, triage outcomes, status changes and filings come only from deterministic rules or explicit human actions.
- Deadlines and `daysRemaining` are computed by the server; clients display them and never compute them.

## Stub status (Phase 4)
Operations tagged `Stub` return 501 once the caller passes authentication, permission and bank-scope checks. So a frontend can already build against the real authorization behavior:

| Situation | Response |
|---|---|
| Caller lacks permission | 403 |
| Another bank's case | 404 |
| Authorized | 501 |

Implemented now:
- `POST /intake/disputes`: portal intake. All ten gates are recorded; see "Intake (Phase 5)" below.
- `GET /disputes/{disputeId}`
- `GET /disputes/{disputeId}/gate-results`: processor and admin users only.
- `GET /cases`, `GET /cases/{caseId}`, `GET /cases/{caseId}/timeline`, `POST /cases/{caseId}/transitions`, `PATCH /cases/{caseId}/assignment`, `POST /cases/{caseId}/retriage`: see Case Management (Phase 7) below.
- `GET /cases/{caseId}/triage`: triage evaluations, newest first. Requires `VIEW_TRIAGE`; processor and admin users only.
- `GET /me`
- `GET /admin/banks`
- `GET /admin/banks/{bankId}`
- `GET /admin/banks/{bankId}/users`
- `GET /admin/permissions`

SDK endpoints return 401 until SDK authentication is approved (ADR-0005).

## Intake (Phase 5 skeleton)

### Validation (400)
Only format is checked; no business rules are applied:
- `cardNumberMasked` must show the last four digits only. Accepted mask characters are `*`, `X`, `x` and `•`.
- Card numbers in any free-text field are rejected.
- `currencyCode` must be in ISO format.
- Amount: at least 0, at most 2 decimal places.
- Field lengths follow the schema.

Missing facts are **not** a 400: deciding required fields is Gate 1's job.

### Channel
Inferred on the server as `PORTAL`. The direct-API channel needs an approved machine-to-machine identity first.

### Gates
- All ten approved ER-diagram gates run in order, and each result is persisted.
- None of their criteria are signed off yet, so every gate records `passed: null` with a reason starting `PENDING_DEFINITION`.
- Each gate result has an `executionStatus`: `Passed`, `Failed`, `PendingDefinition` or `TechnicalError`. A technical error is reported with the distinct reason prefix `GATE_ERROR`. Only `Passed` counts as a successful validation.
- Status rule: all ten pass → `NEW`; otherwise → `FLAGGED` (the analyst queue).
- **Every dispute is therefore FLAGGED until gate criteria are approved.**

### Response
`202 { disputeId, status }` with a `Location` header.

### Events
`dispute.received`, `dispute.gates.evaluated` and `dispute.flagged` (the last only if not all gates passed). They are written atomically with the dispute.

### Retries (ADR-0106, implemented)
Retrying `POST /intake/disputes` with the **same** `Idempotency-Key` and the same request, within 90 days, replays the stored `202` response with `Idempotent-Replayed: true`. No second dispute is created.
- Reusing a key with a different request returns `422 IDEMPOTENCY_KEY_REUSED`.
- Keys are scoped to the calling user and operation.
- Use a new key for each new dispute.

## Triage (Phase 6)

Triage is a platform workflow step, not an API operation. It runs only for disputes in status `NEW` (all ten gates passed), so a FLAGGED dispute is never triaged automatically.

`GET /cases/{caseId}/triage` returns `TriageResultDto[]`:

| Field | Meaning |
|---|---|
| `evaluationStatus` | `Complete` means `outcome` is one of the six outcomes. `Incomplete` means approved rules, bank configuration or facts were missing: `outcome` is `null`, `humanReviewTriggered` is `true` and `humanReviewReason` starts `TRIAGE_INCOMPLETE`. |
| `triageLayer` | The layer that decided: `SCHEME_RULES` (the scheme layer could not determine a code) or `ISSUER_TRIAGE`. |
| `outcome` | `ProceedToFiling`, `AutoRefund`, `RouteToHuman`, `SendToCompliance`, `Invalid` or `Defer`. |

Outcomes are **recommendations**. They never execute a refund, a filing or a status change. The reason code and deadline come only from approved, versioned scheme rules, never from AI.

**With today's production configuration, every triage is Incomplete.** No scheme settings are approved (ADR-0122) and no bank configuration is approved (ADR-0119).

## Case Management (Phase 7)

### How cases are created
A case is created automatically for **every** dispute once its gates are evaluated. Creation happens through the outbox, so it is eventual: poll or refresh after submitting.
- A **NEW** dispute gets a `NEW` case, which is then triaged automatically.
- A **FLAGGED** dispute gets a `FLAGGED` case in the analyst queue (`GET /cases?status=FLAGGED`). It is never triaged automatically.

The reference format is `CB-{YYYY}-{NNNNNN}`, with the number taken from a database sequence. Numbers are increasing, but gaps are possible (ADR-0125).

### Case statuses
Approved values: `NEW`, `FLAGGED`, `UNDER_REVIEW`, `APPROVED`, `REJECTED`, `FILED`, `CLOSED`. The `validActions` field on the case detail lists the lifecycle actions the **caller** may take now, computed by the server from the status, user type and permissions. Render buttons from it; do not hard-code transition logic.

| Action | From → To | Endpoint | Who |
|---|---|---|---|
| `START_REVIEW` | NEW / FLAGGED → UNDER_REVIEW | `POST /cases/{id}/transitions` | `UPDATE_CASE_STATUS` |
| `CLOSE` | REJECTED → CLOSED | `POST /cases/{id}/transitions` (rationale required) | `UPDATE_CASE_STATUS` |
| `CLOSE` | FILED → CLOSED | `POST /cases/{id}/transitions` (rationale required) | `UPDATE_CASE_STATUS` and user type ADMIN; otherwise 403 `USER_TYPE_NOT_ALLOWED` |
| `APPROVE` / `REJECT` | UNDER_REVIEW → APPROVED / REJECTED | `POST /cases/{id}/review/decision` | `REVIEW_CASE` |
| `FILE` | APPROVED → FILED | `POST /filings/{id}/confirmation` | `SUBMIT_MASTERCOM` |
| `FLAG` / `UNFLAG` | none approved yet (ADR-0110) | `POST /cases/{id}/transitions` | Always 422 `INVALID_TRANSITION` |

`APPROVE`, `REJECT` and `FILE` sent to `/transitions` return 422 `INVALID_TRANSITION`.

### Endpoints

| Endpoint | Permission | Notes |
|---|---|---|
| `GET /cases?status&bankId&page&pageSize` | `VIEW_CASES` | Scope-filtered; `daysRemaining` is server-computed (null until the calendar is approved) |
| `GET /cases/{id}` | `VIEW_CASES` | Returns the ETag, the derived reason code (deterministic rules only) and `validActions` |
| `GET /cases/{id}/timeline` | `VIEW_CASES` | The append-only case events, oldest first. Internal: analysts only |
| `POST /cases/{id}/transitions` body `{action, rationale, expectedVersion}` | `UPDATE_CASE_STATUS` | `Idempotency-Key` required (ADR-0106; replays return `Idempotent-Replayed: true`). See the action table above. There is no `PATCH /status` |
| `PATCH /cases/{id}/assignment` body `{assignedTo}` | `ASSIGN_CASE` | `If-Match` required. `null` unassigns. The assignee must be able to view the case's bank |
| `POST /cases/{id}/retriage` body `{reason}` | `UPDATE_CASE_STATUS` | FLAGGED or UNDER_REVIEW cases only; never automatic (ADR-0124) |

All of these endpoints are for processor and admin users only. Bank users get the curated Client Portal view in Phase 11.
