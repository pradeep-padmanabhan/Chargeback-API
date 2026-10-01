# ADR-0106: API idempotency-key store and consumer de-duplication

Status: **Accepted in full (2026-09-29) and implemented.** `submitDispute` uses atomic mode; `confirmFiling` (reservation mode) is a Phase 10 stub. The pre-production blocker is **resolved** for `submitDispute`. · Date: 2026-09-29

## Context
Three operations require an `Idempotency-Key` header today:
- `POST /intake/disputes` (implemented)
- `POST /intake/bulk` (stub)
- `POST /filings/{filingId}/confirmation` (stub)

The header's format is validated, but nothing is stored. A retried request therefore creates a duplicate, a **pre-production blocker** documented by `KNOWN_LIMITATION_ADR0106_…`. Clients are instructed not to retry automatically.

Consumer-side de-duplication (`processed_domain_events`, Phase 7) is separate and already implemented. It is summarized at the end.

## Decision (proposed)

### 1. Storage — migration `0004_idempotency_keys.sql` (not the baseline)
```sql
CREATE TABLE chargeback_diagram.idempotency_keys (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  principal_id     uuid NOT NULL REFERENCES chargeback_diagram.users(id),
  operation        varchar(100) NOT NULL,          -- OpenAPI operationId, e.g. submitDispute
  idempotency_key  varchar(128) NOT NULL,          -- client value, validated [A-Za-z0-9._:-]{8,128}
  request_hash     char(64) NOT NULL,              -- SHA-256 hex of the canonical request (§3)
  state            varchar(16) NOT NULL CHECK (state IN ('IN_PROGRESS','COMPLETED')),
  response_status  integer,                        -- set when COMPLETED
  response_body    jsonb,                          -- set when COMPLETED; small DTO only, never card data
  resource_id      uuid,                           -- e.g. the dispute id, for support/audit
  created_at       timestamptz NOT NULL DEFAULT now(),
  completed_at     timestamptz,
  expires_at       timestamptz NOT NULL,           -- created_at + 90 days (approved)
  CONSTRAINT idempotency_keys_principal_operation_key UNIQUE (principal_id, operation, idempotency_key),
  CHECK (state = 'IN_PROGRESS' OR (response_status IS NOT NULL AND completed_at IS NOT NULL))
);
CREATE INDEX ix_idempotency_keys_expires ON chargeback_diagram.idempotency_keys (expires_at);
```
- **Scope** is per authenticated principal and per operation. One user can never replay, observe or block another user's key, even within the same bank. Machine-to-machine and SDK principals will need their own `principal_id` source once approved (ADR-0005). For them the foreign key to `users` would have to be relaxed.
- **Row contents:** bank id and card data are deliberately not stored.

### 2. Request flow (`IdempotentCommand` marker + `IdempotencyBehavior`)
Idempotency is checked **after** authentication, request-shape validation and authorization, so an unauthorized caller learns nothing. It is checked before any business effect.

| Situation | Result |
|---|---|
| No key, or a malformed key | `400 IDEMPOTENCY_KEY_REQUIRED` (unchanged) |
| New key | The operation executes; the key is stored as `COMPLETED` with the response (see the modes below) |
| Same key, same request hash, `COMPLETED`, not expired | **Replay:** the stored status and body, plus the header `Idempotent-Replayed: true`. **Not re-executed**, even if the resource changed since |
| Same key, **different** request hash | `422 IDEMPOTENCY_KEY_REUSED`; nothing executes |
| Same key while the first request is `IN_PROGRESS` | `409 IDEMPOTENCY_REQUEST_IN_PROGRESS`; the client retries later |
| Same key after `expires_at` | Treated as a new key: expired rows are ignored by lookups whether or not the purge has run yet |
| The operation fails (4xx/5xx) | Nothing is stored in atomic mode, so the client may retry with the same key. In reservation mode the reservation is released, unless the outcome is unknown (below) |

**Two execution modes, chosen per operation:**
- **Atomic** (effect is one database transaction, e.g. `submitDispute`): the `COMPLETED` key row is inserted **in the same transaction** as the business effect. Either both commit or neither does. Two concurrent first-time requests both evaluate, but the UNIQUE constraint lets only one commit. The loser rolls back and returns the winner's stored response as a replay. Gate evaluation may run twice in that race; it has no side effects today.
- **Reservation** (effect includes an external call, e.g. `confirmFiling` to Mastercom, Phase 10):
  1. Insert `IN_PROGRESS` and commit.
  2. Perform the call outside any database transaction, per the external-call rule.
  3. Mark `COMPLETED` with the response.

  If the outcome of the external call is unknown (a timeout), the reservation **stays `IN_PROGRESS`**. The Phase 10 reconciliation (query-before-retry) resolves it. It is never retried blindly.

### 3. Request hash
SHA-256 over:
1. the `operation`;
2. the route values, such as `filingId`;
3. the canonical JSON of the bound request body. The canonical form is serialized with fixed options, with property order taken from the contract type.

For `multipart` bulk uploads, the hash also covers the file's SHA-256. Headers other than the key are excluded. The input is one-way hashed, and bodies already carry masked card data only.

### 4. Stored responses
- **What is stored:** only 2xx responses, only the small response DTO (for example `{disputeId, status}`), and at most 16 KB. Larger responses would store `resource_id` only and replay a `303 See Other` to the resource. No operation needs that today.
- **Card data:** response bodies must never contain card data. A test asserts this for every idempotent operation.

### 5. TTL expiry and the nightly purge (approved: 90 days, nightly)
- **Expiry:** `expires_at = created_at + 90 days`, set at insert. Correctness never depends on the purge, because lookups filter `expires_at > now()`.
- **Purge job:**
  - `IdempotencyKeyPurgeJob` runs as a hosted worker in the API service, nightly at `Idempotency:PurgeTimeUtc`, default `02:00`.
  - It deletes rows with `expires_at <= now()` in batches of `Idempotency:PurgeBatchSize` (default 5,000), each batch in its own short transaction, to avoid long locks.
- **Single runner:** `pg_try_advisory_lock` ensures only one ECS task purges. Others skip that night's run.
- **Logging:** each run logs the start, the rows deleted, the duration and the outcome. No per-row audit: these are technical keys, not business records.
- **On failure:** a run that fails is logged and retried at the next nightly run. A health warning is raised if the last successful purge is more than 48 hours old.
- **Alternative (not recommended now):** an EventBridge-scheduled ECS task. It is operationally cleaner at scale, but adds infrastructure that isn't approved.

### 6. Consumer de-duplication (implemented Phase 7, unchanged)
- **Table:** `processed_domain_events(event_id UNIQUE, consumer, processed_at)`.
- **Recording:** each event is recorded atomically with the consumer's effect; repeats are skipped and logged.
- **Retention is NOT included in the 90-day purge** unless you decide otherwise. Purging a processed-event record would let a later DLQ requeue of that event run again. A safe combined rule: DLQ requeue refuses messages older than the processed-event retention window.

## Consequences
- **Blocker lifted:** the pre-production blocker is lifted for `submitDispute` when this ships. `KNOWN_LIMITATION_ADR0106_…` is **inverted**: the same key twice gives one dispute, a replay, and the `Idempotent-Replayed` header.
- **Client contract:** the "clients must not auto-retry" warning in the contract is replaced by "safe to retry with the same key for 90 days".
- **Pipeline order:** a pipeline behavior is added between Authorization and Transaction, giving Logging → Validation → Authorization → **Idempotency** → Transaction. This changes the approved pipeline order, so it needs explicit approval. The alternative is an endpoint filter, which cannot join the business transaction required by atomic mode.

## Tests (to implement on approval)
**Unit tests**
- request-hash canonicalization, so the same body with different property order or whitespace gives the same hash;
- the mode state machine;
- TTL filtering.

**Integration tests**
- replay returns the identical body with the replay header, and exactly one dispute exists;
- a different body with the same key gives 422, and nothing is created;
- a concurrent duplicate gives one dispute (race via parallel requests);
- the same key from a different user executes independently;
- an expired key is treated as new;
- the purge deletes only expired rows, runs in batches, and is safe on two instances (advisory lock);
- stored responses contain no card data;
- migration 0004 is idempotent.

## Decisions requested
1. Approve the table and migration 0004.
2. Approve **per-principal** scope, versus per-bank.
3. Approve the **new pipeline position** (after Authorization, before Transaction).
4. Approve the 422 and 409 behaviour for key reuse and in-progress requests.
5. Approve the purge as an **in-process hosted job with an advisory lock**, versus EventBridge.
6. Decide whether `processed_domain_events` has a retention period, and how that interacts with DLQ requeue.

## Implementation notes (2026-09-29)
- **Decisions:** all six were approved as proposed. Decision 6: `processed_domain_events` is purged at 90 days in the same nightly run, and DLQ requeue refuses messages older than 90 days (`422 DLQ_MESSAGE_EXPIRED`).
- **Pipeline order** (documented in `ApiServiceRegistration` and enforced by `PipelineOrderTests`): Logging → Validation → Authorization → Idempotency → Transaction.
- **Request hash:** decimals are scale-normalized, so `5`, `5.0` and `5.00` are the same amount.
- **Advisory lock:** the purge uses a **transaction-scoped** advisory lock (`pg_try_advisory_xact_lock`) on a dedicated connection, held for the run. It is released whenever that transaction ends. A session-level lock was found to stay on a pooled connection after close.
- **Health signal:** degraded when purgeable rows are overdue by more than 48 hours. Measuring from the data keeps it correct across ECS instances without a run-history table.
- **Stored-response guard:** it checks string values that are not GUIDs, plus numbers. A whole-text check falsely matched UUIDs whose dash-separated groups were all digits; the concurrency test caught this.
- **Limitation test inverted:** `KNOWN_LIMITATION_ADR0106_…` became `ADR0106_same_idempotency_key_replays_and_creates_no_duplicate_dispute`, after three consecutive green integration runs.
