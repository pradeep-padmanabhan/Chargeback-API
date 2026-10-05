-- Migration 0007 — Evidence & Documents (approved 2026-10-03).
-- Extends the baseline documents table (one source of truth) and adds document_classifications.
-- Applies after 0001 (baseline) and 0002-0006. Idempotent: safe to run more than once.
-- Run: psql -v ON_ERROR_STOP=1 -f 0007_evidence_documents.sql
-- NOT executed by the application.
BEGIN;
SET LOCAL search_path TO chargeback_diagram, public;

-- 1. Permissions. UPLOAD_DOCUMENT is in the original baseline; VIEW_DOCUMENTS is new (the baseline defines it for
--    fresh installs). Both are assigned to the four approved roles.
INSERT INTO permissions(name, resource, action, description) VALUES
 ('UPLOAD_DOCUMENT', 'DOCUMENT', 'CREATE', 'Upload evidence'),
 ('VIEW_DOCUMENTS', 'DOCUMENT', 'READ', 'View documents of permitted cases')
ON CONFLICT (name) DO NOTHING;

INSERT INTO role_permissions(role_id, permission_id)
SELECT r.id, p.id
FROM roles r
JOIN permissions p ON p.name IN ('UPLOAD_DOCUMENT', 'VIEW_DOCUMENTS')
WHERE r.name IN ('Analyst', 'Senior Analyst', 'Compliance Officer', 'Admin')
ON CONFLICT (role_id, permission_id) DO NOTHING;

-- 2. Upload lifecycle, independent of processing. documents.document_status (Pending | Processing | Success | Failed,
--    baseline CHECK) remains the OCR/classification processing status; processing_error holds its failure reason.
ALTER TABLE documents ADD COLUMN IF NOT EXISTS upload_status varchar(16) NOT NULL DEFAULT 'PENDING_UPLOAD'
  CONSTRAINT documents_upload_status_check CHECK (upload_status IN ('PENDING_UPLOAD', 'UPLOADED'));
ALTER TABLE documents ADD COLUMN IF NOT EXISTS upload_confirmed_at timestamptz;
ALTER TABLE documents ADD COLUMN IF NOT EXISTS deleted_at timestamptz;
ALTER TABLE documents ADD COLUMN IF NOT EXISTS deleted_by uuid REFERENCES users(id);
CREATE INDEX IF NOT EXISTS ix_documents_case_live ON documents (case_id, uploaded_at DESC) WHERE deleted_at IS NULL;

-- 3. Immutable upload-stage record (ADR-0104): the declared upload is never rewritten, rows are never deleted
--    (soft delete via deleted_at), upload_status only moves forward and a soft delete is permanent.
CREATE OR REPLACE FUNCTION documents_upload_stage_immutable() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'documents are never deleted; use deleted_at (soft delete)' USING ERRCODE = 'restrict_violation';
  END IF;
  IF NEW.case_id IS DISTINCT FROM OLD.case_id
     OR NEW.file_name IS DISTINCT FROM OLD.file_name
     OR NEW.s3_key IS DISTINCT FROM OLD.s3_key
     OR NEW.mime_type IS DISTINCT FROM OLD.mime_type
     OR NEW.file_size_bytes IS DISTINCT FROM OLD.file_size_bytes
     OR NEW.scheme_stage IS DISTINCT FROM OLD.scheme_stage
     OR NEW.upload_source IS DISTINCT FROM OLD.upload_source
     OR NEW.uploaded_by IS DISTINCT FROM OLD.uploaded_by
     OR NEW.uploaded_at IS DISTINCT FROM OLD.uploaded_at THEN
    RAISE EXCEPTION 'document upload-stage fields are immutable' USING ERRCODE = 'restrict_violation';
  END IF;
  IF OLD.upload_status = 'UPLOADED' AND NEW.upload_status <> 'UPLOADED' THEN
    RAISE EXCEPTION 'document upload_status cannot move backwards' USING ERRCODE = 'restrict_violation';
  END IF;
  IF OLD.upload_confirmed_at IS NOT NULL AND NEW.upload_confirmed_at IS DISTINCT FROM OLD.upload_confirmed_at THEN
    RAISE EXCEPTION 'document upload_confirmed_at is immutable once set' USING ERRCODE = 'restrict_violation';
  END IF;
  IF OLD.deleted_at IS NOT NULL AND (NEW.deleted_at IS DISTINCT FROM OLD.deleted_at OR NEW.deleted_by IS DISTINCT FROM OLD.deleted_by) THEN
    RAISE EXCEPTION 'a soft-deleted document stays deleted' USING ERRCODE = 'restrict_violation';
  END IF;
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS documents_upload_stage_immutable ON documents;
CREATE TRIGGER documents_upload_stage_immutable
  BEFORE UPDATE OR DELETE ON documents
  FOR EACH ROW EXECUTE FUNCTION documents_upload_stage_immutable();

-- 4. One row per classification run (reruns never overwrite). Advisory only; append-only.
CREATE TABLE IF NOT EXISTS document_classifications (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  document_id       uuid NOT NULL REFERENCES documents(id),
  status            varchar(16) NOT NULL CHECK (status IN ('SUCCESS', 'FAILED')),
  category          varchar(100),
  confidence        numeric(5,4) CHECK (confidence IS NULL OR confidence BETWEEN 0 AND 1),
  extracted_fields  jsonb NOT NULL DEFAULT '{}'::jsonb,
  concerns          jsonb NOT NULL DEFAULT '[]'::jsonb,
  failure_reason    varchar(40),
  model_name        varchar(120),
  created_at        timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT document_classifications_outcome_check CHECK (
    (status = 'SUCCESS' AND category IS NOT NULL AND failure_reason IS NULL)
    OR (status = 'FAILED' AND failure_reason IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS ix_document_classifications_document ON document_classifications (document_id, created_at DESC);

CREATE OR REPLACE FUNCTION document_classifications_append_only() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'document_classifications is append-only (% refused)', TG_OP USING ERRCODE = 'restrict_violation';
END $$;

DROP TRIGGER IF EXISTS document_classifications_append_only ON document_classifications;
CREATE TRIGGER document_classifications_append_only
  BEFORE UPDATE OR DELETE ON document_classifications
  FOR EACH ROW EXECUTE FUNCTION document_classifications_append_only();
COMMIT;
-- VALIDATION (read-only):
-- SELECT column_name FROM information_schema.columns WHERE table_schema = 'chargeback_diagram' AND table_name = 'documents' ORDER BY 1;
-- SELECT p.name, count(*) FROM chargeback_diagram.role_permissions rp JOIN chargeback_diagram.permissions p ON p.id = rp.permission_id
--   WHERE p.name IN ('UPLOAD_DOCUMENT', 'VIEW_DOCUMENTS') GROUP BY 1;  -- expect 4 each
