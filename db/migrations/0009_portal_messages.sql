-- Migration 0009 — Client Portal & Communications: case message thread (approved 2026-10-04).
-- Uses the baseline portal_messages table (one source of truth): sender_type BANK (bank user) | PROCESSOR (analyst).
-- Applies after 0001 (baseline) and 0002-0008. Idempotent: safe to run more than once.
-- Run: psql -v ON_ERROR_STOP=1 -f 0009_portal_messages.sql
-- NOT executed by the application.
-- Row-level security is deferred to ADR-0006 (all bank-owned tables together); bank scope is enforced and tested in
-- the application layer meanwhile.
BEGIN;
SET LOCAL search_path TO chargeback_diagram, public;

-- 1. Message body: 1-2000 characters after trimming.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'portal_messages_text_check'
                 AND conrelid = 'chargeback_diagram.portal_messages'::regclass) THEN
    ALTER TABLE chargeback_diagram.portal_messages ADD CONSTRAINT portal_messages_text_check
      CHECK (length(btrim(message_text)) BETWEEN 1 AND 2000);
  END IF;
END $$;

-- 2. Thread reads are per case, oldest first.
CREATE INDEX IF NOT EXISTS ix_portal_messages_case ON portal_messages (case_id, created_at, id);

-- 3. Append-only: messages are never edited or deleted.
CREATE OR REPLACE FUNCTION portal_messages_append_only() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'portal_messages is append-only (% refused)', TG_OP USING ERRCODE = 'restrict_violation';
END $$;

DROP TRIGGER IF EXISTS portal_messages_append_only ON portal_messages;
CREATE TRIGGER portal_messages_append_only
  BEFORE UPDATE OR DELETE ON portal_messages
  FOR EACH ROW EXECUTE FUNCTION portal_messages_append_only();
COMMIT;
-- VALIDATION (read-only):
-- SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'portal_messages_text_check';
-- SELECT tgname FROM pg_trigger WHERE tgname = 'portal_messages_append_only';
