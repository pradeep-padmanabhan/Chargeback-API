-- Migration 0010 — Row-level security for bank-owned tables (ADR-0006, approved 2026-10-04).
-- Applies after 0001 (baseline) and 0002-0009. Idempotent: safe to run more than once.
-- Run: psql -v ON_ERROR_STOP=1 -f 0010_row_level_security.sql (as a role that owns the schema and may create roles).
-- NOT executed by the application.
--
-- How it works:
--   * The application sets two session settings on every connection it opens (and again whenever the request's scope
--     changes): app.scope ('banks' | 'system' | '') and app.bank_ids (a uuid[] literal of the caller's banks).
--   * Every protected table has one FORCE'd policy: visible when app.scope = 'system' (workflow steps, scope resolution)
--     or when the row's bank is in app.bank_ids. Unset settings => no rows (fail closed).
--   * Superusers and BYPASSRLS roles are not subject to RLS. The API must therefore connect as a login that is a member
--     of chargeback_app (no BYPASSRLS); migrations run as chargeback_migrations.
--   * Not protected (no bank data, or needed before the scope is known): roles, permissions, role_permissions, users,
--     user_bank_scopes, banks, scheme_reason_codes, scheme_rule_specs, idempotency_keys, processed_domain_events.
--     domain_events and ai_decision_logs are not protected yet: they are written outside the request scope (outbox,
--     AI client) and user events have no case; see ADR-0006.
--   * KNOWN_LIMITATION_RLS_PRODUCTION_GRANTS_: login roles, passwords (Secrets Manager) and the final tightening of
--     chargeback_app grants are created at deploy time, not here.
BEGIN;
SET LOCAL search_path TO chargeback_diagram, public;

-- 1. Group roles (NOLOGIN; logins are created at deploy time and granted membership). Cluster-wide, so guarded.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'chargeback_app') THEN
    CREATE ROLE chargeback_app NOLOGIN NOBYPASSRLS;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'chargeback_migrations') THEN
    BEGIN
      CREATE ROLE chargeback_migrations NOLOGIN BYPASSRLS;
    EXCEPTION WHEN insufficient_privilege THEN
      RAISE NOTICE 'chargeback_migrations not created: BYPASSRLS needs a superuser. Create it at deploy time.';
    END;
  END IF;
END $$;

GRANT USAGE ON SCHEMA chargeback_diagram TO chargeback_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA chargeback_diagram TO chargeback_app;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA chargeback_diagram TO chargeback_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA chargeback_diagram GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO chargeback_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA chargeback_diagram GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO chargeback_app;

-- The migration runner reads and writes everything (BYPASSRLS); DDL rights come from schema ownership at deploy time.
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'chargeback_migrations') THEN
    GRANT USAGE, CREATE ON SCHEMA chargeback_diagram TO chargeback_migrations;
    GRANT ALL ON ALL TABLES IN SCHEMA chargeback_diagram TO chargeback_migrations;
    GRANT ALL ON ALL SEQUENCES IN SCHEMA chargeback_diagram TO chargeback_migrations;
  END IF;
END $$;

-- 2. Scope helpers. SECURITY INVOKER (default): lookups inside them are themselves subject to RLS.
CREATE OR REPLACE FUNCTION rls_is_system() RETURNS boolean
LANGUAGE sql STABLE AS $$ SELECT coalesce(current_setting('app.scope', true), '') = 'system' $$;

CREATE OR REPLACE FUNCTION rls_bank_ids() RETURNS uuid[]
LANGUAGE sql STABLE AS $$ SELECT coalesce(nullif(current_setting('app.bank_ids', true), '')::uuid[], '{}'::uuid[]) $$;

CREATE OR REPLACE FUNCTION rls_dispute_visible(p_dispute_id uuid) RETURNS boolean
LANGUAGE sql STABLE AS $$
  SELECT chargeback_diagram.rls_is_system()
      OR EXISTS (SELECT 1 FROM chargeback_diagram.disputes d
                 WHERE d.id = p_dispute_id AND d.bank_id = ANY(chargeback_diagram.rls_bank_ids()))
$$;

CREATE OR REPLACE FUNCTION rls_case_visible(p_case_id uuid) RETURNS boolean
LANGUAGE sql STABLE AS $$
  SELECT chargeback_diagram.rls_is_system()
      OR EXISTS (SELECT 1 FROM chargeback_diagram.cases c
                 JOIN chargeback_diagram.disputes d ON d.id = c.dispute_id
                 WHERE c.id = p_case_id AND d.bank_id = ANY(chargeback_diagram.rls_bank_ids()))
$$;

-- 3. Enable + FORCE RLS and (re)create one policy per protected table.
DO $$
DECLARE
  t record;
BEGIN
  FOR t IN SELECT * FROM (VALUES
      ('disputes',                 'chargeback_diagram.rls_is_system() OR bank_id = ANY(chargeback_diagram.rls_bank_ids())'),
      ('cases',                    'chargeback_diagram.rls_dispute_visible(dispute_id)'),
      ('gate_results',             'chargeback_diagram.rls_dispute_visible(dispute_id)'),
      ('triage_results',           'chargeback_diagram.rls_case_visible(case_id)'),
      ('document_slots',           'chargeback_diagram.rls_case_visible(case_id)'),
      ('documents',                'chargeback_diagram.rls_case_visible(case_id)'),
      ('document_classifications', 'chargeback_diagram.rls_is_system() OR EXISTS (SELECT 1 FROM chargeback_diagram.documents doc WHERE doc.id = document_id AND chargeback_diagram.rls_case_visible(doc.case_id))'),
      ('case_review_decisions',    'chargeback_diagram.rls_case_visible(case_id)'),
      ('portal_messages',          'chargeback_diagram.rls_case_visible(case_id)'),
      ('zendesk_tickets',          'chargeback_diagram.rls_case_visible(case_id)'),
      ('mastercom_filings',        'chargeback_diagram.rls_case_visible(case_id)'),
      ('filing_api_log',           'chargeback_diagram.rls_is_system() OR EXISTS (SELECT 1 FROM chargeback_diagram.mastercom_filings f WHERE f.id = filing_id AND chargeback_diagram.rls_case_visible(f.case_id))')
    ) AS v(table_name, predicate)
  LOOP
    EXECUTE format('ALTER TABLE chargeback_diagram.%I ENABLE ROW LEVEL SECURITY', t.table_name);
    EXECUTE format('ALTER TABLE chargeback_diagram.%I FORCE ROW LEVEL SECURITY', t.table_name);
    EXECUTE format('DROP POLICY IF EXISTS bank_scope ON chargeback_diagram.%I', t.table_name);
    EXECUTE format('CREATE POLICY bank_scope ON chargeback_diagram.%I FOR ALL USING (%s) WITH CHECK (%s)',
                   t.table_name, t.predicate, t.predicate);
  END LOOP;
END $$;
COMMIT;
-- VALIDATION (read-only):
-- SELECT relname, relrowsecurity, relforcerowsecurity FROM pg_class
--   WHERE relnamespace = 'chargeback_diagram'::regnamespace AND relrowsecurity ORDER BY 1;   -- expect 12 rows, both true
-- SELECT tablename, policyname FROM pg_policies WHERE schemaname = 'chargeback_diagram' ORDER BY 1;
