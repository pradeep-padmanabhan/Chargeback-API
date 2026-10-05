-- Migration 0011 — Row-level security for domain_events and ai_decision_logs (guide §8 #42, approved 2026-10-05).
-- Applies after 0001 (baseline) and 0002-0010. Idempotent: safe to run more than once.
-- Run: psql -v ON_ERROR_STOP=1 -f 0011_rls_events_and_ai_logs.sql
-- NOT executed by the application.
--
-- Both tables get a bank_id column (no FK: audit/outbox rows must never fail on a bank's later state), backfilled from
-- the event envelope (domain_events.event_data.bankId) and from the case (ai_decision_logs.case_id). Rows without a
-- bank (e.g. a log written with no case) are visible to system scope only. The outbox dispatcher and the AI audit
-- writer use their own connections and run in system scope.
BEGIN;
SET LOCAL search_path TO chargeback_diagram, public;

-- A schema owner subject to FORCE'd RLS must still see every row while backfilling.
SELECT set_config('app.scope', 'system', true);

ALTER TABLE domain_events ADD COLUMN IF NOT EXISTS bank_id uuid;
ALTER TABLE ai_decision_logs ADD COLUMN IF NOT EXISTS bank_id uuid;

UPDATE domain_events
SET bank_id = (event_data->>'bankId')::uuid
WHERE bank_id IS NULL AND nullif(event_data->>'bankId', '') IS NOT NULL;

UPDATE ai_decision_logs a
SET bank_id = d.bank_id
FROM cases c
JOIN disputes d ON d.id = c.dispute_id
WHERE a.case_id = c.id AND a.bank_id IS NULL;

CREATE INDEX IF NOT EXISTS ix_domain_events_bank ON domain_events (bank_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_ai_decision_logs_bank ON ai_decision_logs (bank_id, created_at DESC);

DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['domain_events', 'ai_decision_logs']
  LOOP
    EXECUTE format('ALTER TABLE chargeback_diagram.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE chargeback_diagram.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('DROP POLICY IF EXISTS bank_scope ON chargeback_diagram.%I', t);
    EXECUTE format(
      'CREATE POLICY bank_scope ON chargeback_diagram.%I FOR ALL '
      'USING (chargeback_diagram.rls_is_system() OR bank_id = ANY(chargeback_diagram.rls_bank_ids())) '
      'WITH CHECK (chargeback_diagram.rls_is_system() OR bank_id = ANY(chargeback_diagram.rls_bank_ids()))', t);
  END LOOP;
END $$;
COMMIT;
-- VALIDATION (read-only):
-- SELECT relname FROM pg_class WHERE relnamespace = 'chargeback_diagram'::regnamespace AND relforcerowsecurity ORDER BY 1;  -- expect 14
-- SELECT count(*) FROM chargeback_diagram.domain_events WHERE bank_id IS NULL;
