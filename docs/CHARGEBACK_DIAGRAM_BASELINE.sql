-- CHARGEBACK DIAGRAM MVP BASELINE, PostgreSQL 16+
-- Run ONLY on a NEW development database: psql -v ON_ERROR_STOP=1 -f CHARGEBACK_DIAGRAM_BASELINE.sql
-- NOT a migration for the separate expanded 110-feature canonical schema.
-- Does not create application login roles, enable RLS, or insert scheme rule values;
-- production deployment MUST configure and test those controls separately.
-- This is migration 0001. A complete install is this file followed by every db/migrations/NNNN_*.sql in order
-- (0002-0008); roles and role -> permission assignments (guide §3.1) are seeded by migrations 0005-0008, never by this file.
BEGIN;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE SCHEMA IF NOT EXISTS chargeback_diagram;
SET LOCAL search_path TO chargeback_diagram, public;

CREATE TABLE banks (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), bank_code varchar(40) NOT NULL UNIQUE,
 bank_name varchar(200) NOT NULL, country varchar(80), status varchar(24) NOT NULL DEFAULT 'ACTIVE',
 created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE roles (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), name varchar(100) NOT NULL UNIQUE,
 description text, role_type varchar(16) NOT NULL CHECK (role_type IN ('PROCESSOR','BANK','ADMIN')),
 is_active boolean NOT NULL DEFAULT true, created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE permissions (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), name varchar(100) NOT NULL UNIQUE,
 resource varchar(60) NOT NULL, action varchar(40) NOT NULL, description text,
 is_active boolean NOT NULL DEFAULT true, created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE role_permissions (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), role_id uuid NOT NULL REFERENCES roles(id),
 permission_id uuid NOT NULL REFERENCES permissions(id), created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(role_id,permission_id)
);
CREATE TABLE users (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), bank_id uuid REFERENCES banks(id),
 cognito_sub varchar(100) NOT NULL UNIQUE, email varchar(254) NOT NULL, full_name varchar(200) NOT NULL,
 user_type varchar(16) NOT NULL CHECK (user_type IN ('PROCESSOR','BANK','ADMIN')),
 role_id uuid NOT NULL REFERENCES roles(id), status varchar(20) NOT NULL DEFAULT 'ACTIVE'
 CHECK (status IN ('ACTIVE','DISABLED','LOCKED')),
 created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
 CONSTRAINT users_bank_scope CHECK ((user_type='BANK' AND bank_id IS NOT NULL) OR (user_type<>'BANK' AND bank_id IS NULL))
);
-- Added security-critical bridge: NULL bank_id does NOT imply access to every bank.
CREATE TABLE user_bank_scopes (
 user_id uuid NOT NULL REFERENCES users(id), bank_id uuid NOT NULL REFERENCES banks(id),
 granted_by uuid REFERENCES users(id), valid_from timestamptz NOT NULL DEFAULT now(),
 valid_until timestamptz, created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY (user_id,bank_id), CHECK(valid_until IS NULL OR valid_until > valid_from)
);
CREATE TABLE scheme_reason_codes (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), code varchar(16) NOT NULL,
 description text NOT NULL, category varchar(80), effective_from date NOT NULL,
 effective_to date, created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(code,effective_from), CHECK(effective_to IS NULL OR effective_to >= effective_from)
);
CREATE TABLE scheme_rule_specs (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), reason_code_id uuid NOT NULL REFERENCES scheme_reason_codes(id),
 scenario varchar(160) NOT NULL, conditions_json jsonb NOT NULL DEFAULT '{}'::jsonb,
 required_docs jsonb NOT NULL DEFAULT '[]'::jsonb, time_limit_days integer,
 effective_from date NOT NULL, effective_to date,
 approval_status varchar(16) NOT NULL DEFAULT 'DRAFT' CHECK(approval_status IN ('DRAFT','APPROVED','RETIRED')),
 created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
 CHECK(time_limit_days IS NULL OR time_limit_days >= 0), CHECK(effective_to IS NULL OR effective_to >= effective_from)
);
CREATE TABLE disputes (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), bank_id uuid NOT NULL REFERENCES banks(id),
 cardholder_reference varchar(100), card_number_masked varchar(32), transaction_date timestamptz,
 transaction_amount numeric(18,2) CHECK(transaction_amount IS NULL OR transaction_amount >= 0),
 currency_code char(3), acquirer_reference_number varchar(100), merchant_name varchar(200),
 intake_channel varchar(30) NOT NULL CHECK(intake_channel IN ('PORTAL','EMAIL','BULK','API','SDK')),
 status varchar(32) NOT NULL DEFAULT 'NEW', created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(bank_id,id)
);
CREATE TABLE gate_results (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), dispute_id uuid NOT NULL REFERENCES disputes(id),
 gate_number integer NOT NULL CHECK(gate_number BETWEEN 1 AND 10), gate_name varchar(100) NOT NULL,
 passed boolean, flag_reason text, checked_at timestamptz, UNIQUE(dispute_id,gate_number)
);
CREATE TABLE cases (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), dispute_id uuid NOT NULL UNIQUE REFERENCES disputes(id),
 case_reference varchar(100) NOT NULL UNIQUE, assigned_to uuid REFERENCES users(id),
 priority varchar(20), status varchar(40) NOT NULL DEFAULT 'NEW',
 derived_reason_code uuid REFERENCES scheme_reason_codes(id), clock_start_date date, filing_deadline_date date,
 days_remaining integer, scheme_function_code varchar(40), ai_summary text,
 human_review_verdict varchar(80), human_reviewed_by uuid REFERENCES users(id), human_reviewed_at timestamptz,
 created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE triage_results (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), case_id uuid NOT NULL REFERENCES cases(id),
 triage_layer varchar(40), hard_eligibility_pass boolean, routing_policy_outcome varchar(80),
 risk_score numeric(8,4), risk_flags jsonb NOT NULL DEFAULT '[]'::jsonb,
 human_review_triggered boolean NOT NULL DEFAULT false, human_review_reason text,
 outcome varchar(40) CHECK(outcome IN ('ProceedToFiling','AutoRefund','RouteToHuman','SendToCompliance','Invalid','Defer')),
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE document_slots (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), case_id uuid NOT NULL REFERENCES cases(id),
 slot_name varchar(150) NOT NULL, is_required boolean NOT NULL DEFAULT false,
 expected_type varchar(80), created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(case_id,slot_name)
);
CREATE TABLE documents (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), case_id uuid NOT NULL REFERENCES cases(id),
 document_slot_id uuid REFERENCES document_slots(id), file_name varchar(255) NOT NULL,
 s3_key text NOT NULL, mime_type varchar(100), file_size_bytes bigint CHECK(file_size_bytes IS NULL OR file_size_bytes >= 0),
 textract_job_id varchar(160), extracted_text text, ai_classification varchar(100),
 ai_confidence numeric(5,4) CHECK(ai_confidence IS NULL OR ai_confidence BETWEEN 0 AND 1),
 scheme_stage varchar(24) NOT NULL CHECK(scheme_stage IN ('Initial','PreArbitration','Arbitration')),
 document_status varchar(16) NOT NULL DEFAULT 'Pending'
 CHECK(document_status IN ('Pending','Processing','Success','Failed')),
 upload_source varchar(40), uploaded_by uuid REFERENCES users(id), uploaded_at timestamptz NOT NULL DEFAULT now(),
 processed_at timestamptz, processing_error text,
 created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE portal_messages (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), case_id uuid NOT NULL REFERENCES cases(id),
 sender_type varchar(16) NOT NULL CHECK(sender_type IN ('BANK','PROCESSOR')),
 sender_id uuid REFERENCES users(id), message_text text NOT NULL, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE zendesk_tickets (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), case_id uuid NOT NULL REFERENCES cases(id),
 zendesk_ticket_id varchar(100) NOT NULL UNIQUE, status varchar(40),
 created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE zendesk_events (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), zendesk_ticket_id uuid NOT NULL REFERENCES zendesk_tickets(id),
 event_type varchar(80), event_data jsonb NOT NULL DEFAULT '{}'::jsonb,
 hmac_verified boolean NOT NULL DEFAULT false, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE mastercom_filings (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), case_id uuid NOT NULL REFERENCES cases(id),
 mastercom_reference varchar(160), filing_status varchar(60), current_stage varchar(60),
 idempotency_key varchar(160) NOT NULL UNIQUE, submitted_at timestamptz,
 created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE filing_api_log (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), filing_id uuid REFERENCES mastercom_filings(id),
 direction varchar(12) NOT NULL CHECK(direction IN ('OUTBOUND','INBOUND')),
 endpoint varchar(500), request_payload jsonb, response_payload jsonb, http_status integer,
 latency_ms integer, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE ai_decision_logs (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), case_id uuid REFERENCES cases(id),
 agent_name varchar(100) NOT NULL, capability_name varchar(100) NOT NULL,
 model_name varchar(120), prompt_template_id varchar(120), input_hash varchar(128),
 raw_response jsonb, parsed_output jsonb, latency_ms integer,
 token_count_input integer, token_count_output integer, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE domain_events (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), case_id uuid REFERENCES cases(id),
 event_type varchar(160) NOT NULL, event_data jsonb NOT NULL DEFAULT '{}'::jsonb,
 published_at timestamptz, created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_users_bank ON users(bank_id);
CREATE INDEX ix_scope_bank ON user_bank_scopes(bank_id);
CREATE INDEX ix_disputes_bank ON disputes(bank_id,created_at DESC);
CREATE INDEX ix_gate_dispute ON gate_results(dispute_id);
CREATE INDEX ix_cases_status ON cases(status,created_at DESC);
CREATE INDEX ix_docs_case_stage_status ON documents(case_id,scheme_stage,document_status);
CREATE INDEX ix_ai_case ON ai_decision_logs(case_id,created_at DESC);
CREATE INDEX ix_outbox_unpublished ON domain_events(created_at) WHERE published_at IS NULL;
CREATE INDEX ix_rules_effective ON scheme_rule_specs(reason_code_id,effective_from,effective_to);

-- Example permission definitions only: role assignment must be approved by product/security owners.
INSERT INTO permissions(name,resource,action,description) VALUES
 ('VIEW_BANKS','BANK','READ','View permitted bank records'),
 ('VIEW_BANK_USERS','USER','READ','View users in permitted banks'),
 ('CREATE_BANK_USER','USER','CREATE','Create users in permitted banks'),
 ('UPDATE_BANK_USER','USER','UPDATE','Update users in permitted banks'),
 ('DISABLE_BANK_USER','USER','DISABLE','Disable users in permitted banks'),
 ('VIEW_CASES','CASE','READ','View permitted cases'),
 ('UPDATE_CASE_STATUS','CASE','UPDATE','Update permitted case status'),
 ('UPLOAD_DOCUMENT','DOCUMENT','CREATE','Upload evidence'),
 ('SUBMIT_MASTERCOM','FILING','CREATE','Submit approved filing'),
 ('VIEW_REPORTS','REPORT','READ','Read permitted reports'),
 -- ADR-0111 (approved 2026-09-29): permission definition only; not assigned to any role.
 ('CREATE_DISPUTE','DISPUTE','CREATE','Submit disputes for permitted banks'),
 -- Phase 7 decision (2026-09-29): permission definition only; not assigned to any role.
 ('ASSIGN_CASE','CASE','ASSIGN','Assign permitted cases to analysts'),
 -- Guide v1.4 §3.1 (approved 2026-09-29): definitions only here; the approved role matrix is seeded by migration 0005.
 ('VIEW_TRIAGE','TRIAGE','READ','View triage results for permitted cases'),
 ('RETRIAGE_CASE','CASE','RETRIAGE','Request manual re-triage of permitted cases'),
 -- Human Review decision (2026-10-03): definition only here; assigned to roles by migration 0006.
 ('REVIEW_CASE','CASE','REVIEW','Review permitted cases and record approve/reject decisions'),
 -- Evidence & Documents decision (2026-10-03): definition only here; assigned to roles by migration 0007.
 ('VIEW_DOCUMENTS','DOCUMENT','READ','View documents of permitted cases'),
 -- Admin & Configuration decision (2026-10-03): definition only here; assigned to roles by migration 0008.
 ('MANAGE_BANK_USERS','USER','MANAGE','Invite, update and remove users of permitted banks');
COMMIT;
-- VALIDATION (read-only, run after script):
-- SELECT table_name FROM information_schema.tables WHERE table_schema='chargeback_diagram' ORDER BY 1;
-- SELECT name FROM chargeback_diagram.permissions ORDER BY name;
-- SECURITY TODO before shared environments: dedicated least-privilege DB users, RLS, append-only audit,
-- no PAN/CVV, sensitive field redaction, tenant-aware queries, idempotent consumers, and migration tooling.
