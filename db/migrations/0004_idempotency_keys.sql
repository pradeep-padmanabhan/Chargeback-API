-- Migration 0004 — ADR-0106 API idempotency-key store (approved 2026-09-29).
-- Applies after 0001 (baseline) and 0002-0003. Idempotent and additive: safe to run more than once;
-- does not modify the baseline or existing tables. Run: psql -v ON_ERROR_STOP=1 -f 0004_idempotency_keys.sql
-- NOT executed by the application.
BEGIN;
SET LOCAL search_path TO chargeback_diagram, public;

-- Stored results of idempotent API operations, scoped per authenticated principal and operation.
-- Expire 90 days after creation (approved TTL); purged nightly by IdempotencyKeyPurgeJob.
CREATE TABLE IF NOT EXISTS idempotency_keys (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  principal_id     uuid NOT NULL REFERENCES users(id),
  operation        varchar(100) NOT NULL,
  idempotency_key  varchar(128) NOT NULL,
  request_hash     char(64) NOT NULL,
  state            varchar(16) NOT NULL CHECK (state IN ('IN_PROGRESS','COMPLETED')),
  response_status  integer,
  response_body    jsonb,
  resource_id      uuid,
  created_at       timestamptz NOT NULL DEFAULT now(),
  completed_at     timestamptz,
  expires_at       timestamptz NOT NULL,
  CONSTRAINT idempotency_keys_principal_operation_key UNIQUE (principal_id, operation, idempotency_key),
  CONSTRAINT idempotency_keys_completed_check CHECK (state = 'IN_PROGRESS' OR (response_status IS NOT NULL AND completed_at IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS ix_idempotency_keys_expires ON idempotency_keys (expires_at);

-- ADR-0106 decision 6 (approved): processed_domain_events is purged at 90 days in the same nightly run.
CREATE INDEX IF NOT EXISTS ix_processed_domain_events_processed_at ON processed_domain_events (processed_at);
COMMIT;
-- VALIDATION (read-only):
-- SELECT count(*) FROM chargeback_diagram.idempotency_keys WHERE expires_at <= now();
