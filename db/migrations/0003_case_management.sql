-- Migration 0003 — Phase 7 Case Management (decisions approved 2026-09-29).
-- Applies after 0001 (baseline) and 0002. Idempotent: safe to run more than once.
-- Run: psql -v ON_ERROR_STOP=1 -f 0003_case_management.sql
-- NOT executed by the application.
BEGIN;
SET LOCAL search_path TO chargeback_diagram, public;

-- 1. ASSIGN_CASE permission (VIEW_CASES and UPDATE_CASE_STATUS already exist in the baseline).
--    Definition only: NOT assigned to any role (role assignment needs product + security approval).
INSERT INTO permissions(name, resource, action, description)
VALUES ('ASSIGN_CASE', 'CASE', 'ASSIGN', 'Assign permitted cases to analysts')
ON CONFLICT (name) DO NOTHING;

-- 2. ADR-0110: approved case status values. Allowed transitions are enforced in the application layer.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'cases_status_check'
                 AND conrelid = 'chargeback_diagram.cases'::regclass) THEN
    ALTER TABLE chargeback_diagram.cases ADD CONSTRAINT cases_status_check
      CHECK (status IN ('NEW','FLAGGED','UNDER_REVIEW','APPROVED','REJECTED','FILED','CLOSED'));
  END IF;
END $$;

-- 3. Case reference numbers: CB-{YYYY}-{6-digit sequence}. The number comes from this sequence.
--    Capacity is 999999 (six digits); nextval fails loudly when exhausted rather than changing the format.
--    Whether the number resets each year is an open question (ADR-0125).
CREATE SEQUENCE IF NOT EXISTS case_reference_seq AS bigint START WITH 1 MINVALUE 1 MAXVALUE 999999 NO CYCLE;

-- 4. ADR-0106 extension: consumed outbox events, for idempotent consumers.
CREATE TABLE IF NOT EXISTS processed_domain_events (
  event_id uuid NOT NULL,
  consumer varchar(100) NOT NULL,
  processed_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT processed_domain_events_event_id_key UNIQUE (event_id)
);
COMMIT;
-- VALIDATION (read-only):
-- SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'cases_status_check';
-- SELECT last_value, is_called FROM chargeback_diagram.case_reference_seq;
-- SELECT count(*) FROM chargeback_diagram.processed_domain_events;
