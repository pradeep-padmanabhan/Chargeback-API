-- Migration 0002 — ADR-0111 (approved 2026-09-29): add the CREATE_DISPUTE permission definition.
-- Target: an EXISTING development database created from the original baseline (0001 =
--         docs/CHARGEBACK_DIAGRAM_BASELINE.sql before this change). Fresh installs already contain it.
-- Idempotent: safe to run more than once. Run: psql -v ON_ERROR_STOP=1 -f 0002_add_create_dispute_permission.sql
-- Defines the permission ONLY. It is deliberately NOT assigned to any role: role assignment requires
-- product and security approval.
-- NOT executed by the application. Migration tooling (Flyway / DbUp / other) is still an open decision.
BEGIN;
INSERT INTO chargeback_diagram.permissions(name, resource, action, description)
VALUES ('CREATE_DISPUTE', 'DISPUTE', 'CREATE', 'Submit disputes for permitted banks')
ON CONFLICT (name) DO NOTHING;
COMMIT;
-- VALIDATION (read-only):
-- SELECT name, resource, action, is_active FROM chargeback_diagram.permissions WHERE name = 'CREATE_DISPUTE';
-- SELECT count(*) FROM chargeback_diagram.role_permissions rp JOIN chargeback_diagram.permissions p ON p.id = rp.permission_id WHERE p.name = 'CREATE_DISPUTE';  -- expect 0
