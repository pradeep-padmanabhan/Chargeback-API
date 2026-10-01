-- Migration 0005 — approved permission codes and role matrix (common guide v1.4 §3.1, approved 2026-09-29).
-- Applies after 0001 (baseline) and 0002-0004. Idempotent: safe to run more than once.
-- Run: psql -v ON_ERROR_STOP=1 -f 0005_role_permission_matrix.sql
-- NOT executed by the application.
-- Seeds exactly the approved matrix. BANK users hold none of these permissions (they use userType = BANK scope),
-- and no role receives implicit bank access: bank scope still comes only from user_bank_scopes.
-- The catalogue for non-case endpoints (intake, documents, filing, admin, SDK) is still open (guide §8 #4);
-- CREATE_DISPUTE and the other baseline permissions are deliberately left unassigned.
BEGIN;
SET LOCAL search_path TO chargeback_diagram, public;

-- 1. New permission definitions (fresh installs already have them from the baseline).
INSERT INTO permissions(name, resource, action, description) VALUES
 ('VIEW_TRIAGE', 'TRIAGE', 'READ', 'View triage results for permitted cases'),
 ('RETRIAGE_CASE', 'CASE', 'RETRIAGE', 'Request manual re-triage of permitted cases')
ON CONFLICT (name) DO NOTHING;

-- 2. Approved roles.
INSERT INTO roles(name, description, role_type) VALUES
 ('Analyst', 'Processor analyst', 'PROCESSOR'),
 ('Senior Analyst', 'Processor senior analyst: assignment and re-triage', 'PROCESSOR'),
 ('Compliance Officer', 'Processor compliance officer', 'PROCESSOR'),
 ('Admin', 'Platform administrator', 'ADMIN')
ON CONFLICT (name) DO NOTHING;

-- 3. Approved role -> permission matrix (guide §3.1).
INSERT INTO role_permissions(role_id, permission_id)
SELECT r.id, p.id
FROM (VALUES
  ('Analyst', 'VIEW_CASES'), ('Analyst', 'UPDATE_CASE_STATUS'), ('Analyst', 'VIEW_TRIAGE'), ('Analyst', 'VIEW_BANK_USERS'),
  ('Senior Analyst', 'VIEW_CASES'), ('Senior Analyst', 'UPDATE_CASE_STATUS'), ('Senior Analyst', 'ASSIGN_CASE'),
  ('Senior Analyst', 'VIEW_TRIAGE'), ('Senior Analyst', 'RETRIAGE_CASE'), ('Senior Analyst', 'VIEW_BANK_USERS'),
  ('Compliance Officer', 'VIEW_CASES'), ('Compliance Officer', 'UPDATE_CASE_STATUS'), ('Compliance Officer', 'VIEW_TRIAGE'),
  ('Compliance Officer', 'VIEW_BANK_USERS'),
  ('Admin', 'VIEW_CASES'), ('Admin', 'UPDATE_CASE_STATUS'), ('Admin', 'ASSIGN_CASE'), ('Admin', 'VIEW_TRIAGE'),
  ('Admin', 'RETRIAGE_CASE'), ('Admin', 'VIEW_BANK_USERS')
) AS m(role_name, permission_name)
JOIN roles r ON r.name = m.role_name
JOIN permissions p ON p.name = m.permission_name
ON CONFLICT (role_id, permission_id) DO NOTHING;
COMMIT;
-- VALIDATION (read-only):
-- SELECT r.name, p.name FROM chargeback_diagram.role_permissions rp
--   JOIN chargeback_diagram.roles r ON r.id = rp.role_id JOIN chargeback_diagram.permissions p ON p.id = rp.permission_id
--   WHERE r.name IN ('Analyst','Senior Analyst','Compliance Officer','Admin') ORDER BY 1, 2;  -- expect 20 rows
