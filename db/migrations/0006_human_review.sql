-- Migration 0006 — Human Review (ADR-0101 option 2 and REVIEW_CASE, approved 2026-10-03).
-- Applies after 0001 (baseline) and 0002-0005. Idempotent: safe to run more than once.
-- Run: psql -v ON_ERROR_STOP=1 -f 0006_human_review.sql
-- NOT executed by the application.
BEGIN;
SET LOCAL search_path TO chargeback_diagram, public;

-- 1. REVIEW_CASE (fresh installs already have the definition from the baseline), assigned to all four approved roles.
INSERT INTO permissions(name, resource, action, description)
VALUES ('REVIEW_CASE', 'CASE', 'REVIEW', 'Review permitted cases and record approve/reject decisions')
ON CONFLICT (name) DO NOTHING;

INSERT INTO role_permissions(role_id, permission_id)
SELECT r.id, p.id
FROM roles r
JOIN permissions p ON p.name = 'REVIEW_CASE'
WHERE r.name IN ('Analyst', 'Senior Analyst', 'Compliance Officer', 'Admin')
ON CONFLICT (role_id, permission_id) DO NOTHING;

-- 2. Append-only audit of every human review decision (ADR-0101 option 2). cases.human_review_* mirror the latest.
--    An APPROVED decision always records the deterministic reason code the analyst confirmed.
CREATE TABLE IF NOT EXISTS case_review_decisions (
  id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  case_id         uuid NOT NULL REFERENCES cases(id),
  decision        varchar(16) NOT NULL CHECK (decision IN ('APPROVED','REJECTED')),
  reason_code_id  uuid REFERENCES scheme_reason_codes(id),
  rationale       text NOT NULL CHECK (length(btrim(rationale)) > 0),
  reviewed_by     uuid NOT NULL REFERENCES users(id),
  decided_at      timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT case_review_decisions_reason_code_check CHECK (decision = 'REJECTED' OR reason_code_id IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS ix_case_review_decisions_case ON case_review_decisions (case_id, decided_at DESC);

-- 3. Append-only: decisions are never updated or deleted.
CREATE OR REPLACE FUNCTION case_review_decisions_append_only() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'case_review_decisions is append-only (% refused)', TG_OP USING ERRCODE = 'restrict_violation';
END $$;

DROP TRIGGER IF EXISTS case_review_decisions_append_only ON case_review_decisions;
CREATE TRIGGER case_review_decisions_append_only
  BEFORE UPDATE OR DELETE ON case_review_decisions
  FOR EACH ROW EXECUTE FUNCTION case_review_decisions_append_only();
COMMIT;
-- VALIDATION (read-only):
-- SELECT r.name FROM chargeback_diagram.role_permissions rp JOIN chargeback_diagram.roles r ON r.id = rp.role_id
--   JOIN chargeback_diagram.permissions p ON p.id = rp.permission_id WHERE p.name = 'REVIEW_CASE' ORDER BY 1;  -- expect 4
-- SELECT count(*) FROM chargeback_diagram.case_review_decisions;
