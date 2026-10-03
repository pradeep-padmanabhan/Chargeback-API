-- Migration 0008 — Admin & Configuration: bank user management (approved 2026-10-03).
-- Applies after 0001 (baseline) and 0002-0007. Idempotent: safe to run more than once.
-- Run: psql -v ON_ERROR_STOP=1 -f 0008_admin_user_management.sql
-- NOT executed by the application.
BEGIN;
SET LOCAL search_path TO chargeback_diagram, public;

-- 1. MANAGE_BANK_USERS (fresh installs already have the definition from the baseline), for the four approved roles.
INSERT INTO permissions(name, resource, action, description)
VALUES ('MANAGE_BANK_USERS', 'USER', 'MANAGE', 'Invite, update and remove users of permitted banks')
ON CONFLICT (name) DO NOTHING;

INSERT INTO role_permissions(role_id, permission_id)
SELECT r.id, p.id
FROM roles r
JOIN permissions p ON p.name = 'MANAGE_BANK_USERS'
WHERE r.name IN ('Analyst', 'Senior Analyst', 'Compliance Officer', 'Admin')
ON CONFLICT (role_id, permission_id) DO NOTHING;

-- 2. The role for bank users. A user's role type must match its user type, and BANK users hold no permissions
--    (guide §3.1), so this role is granted nothing. A bank-admin role is deferred pending product approval.
INSERT INTO roles(name, description, role_type)
VALUES ('Bank User', 'Bank client user (Client Portal); holds no permissions', 'BANK')
ON CONFLICT (name) DO NOTHING;

-- 3. Invitations and soft delete. An invited user has a placeholder cognito_sub ('pending-invite:<user id>') and stays
--    DISABLED until a Cognito identity-linking flow exists (open item). A removed user keeps its row: deleted_at is set
--    and the status is DISABLED.
ALTER TABLE users ADD COLUMN IF NOT EXISTS invited_at timestamptz;
ALTER TABLE users ADD COLUMN IF NOT EXISTS deleted_at timestamptz;
ALTER TABLE users ADD COLUMN IF NOT EXISTS deleted_by uuid REFERENCES users(id);
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'users_deleted_disabled_check'
                 AND conrelid = 'chargeback_diagram.users'::regclass) THEN
    ALTER TABLE chargeback_diagram.users ADD CONSTRAINT users_deleted_disabled_check
      CHECK (deleted_at IS NULL OR status = 'DISABLED');
  END IF;
END $$;
CREATE INDEX IF NOT EXISTS ix_users_bank_live ON users (bank_id, created_at DESC) WHERE deleted_at IS NULL;
COMMIT;
-- VALIDATION (read-only):
-- SELECT r.name FROM chargeback_diagram.role_permissions rp JOIN chargeback_diagram.roles r ON r.id = rp.role_id
--   JOIN chargeback_diagram.permissions p ON p.id = rp.permission_id WHERE p.name = 'MANAGE_BANK_USERS' ORDER BY 1;  -- expect 4
-- SELECT name, role_type FROM chargeback_diagram.roles WHERE name = 'Bank User';
