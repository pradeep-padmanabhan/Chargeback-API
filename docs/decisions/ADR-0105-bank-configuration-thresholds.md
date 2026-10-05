# ADR-0105: Bank configuration and triage thresholds

Status: Superseded by **ADR-0119** (2026-09-29) · Date: 2026-09-29

## Gap
The Issuer Triage Engine applies "approved bank configuration": hard eligibility, routing policy, risk scoring and human-review triggers. Admin is expected to manage "configurable thresholds". The baseline has no table for per-bank configuration or thresholds, and no versioning or approval for them.

## Proposal
A versioned `bank_triage_configs` table:
- bank_id, version, config jsonb;
- approval_status (DRAFT/APPROVED/RETIRED), with the same lifecycle as `scheme_rule_specs`;
- effective_from/to;
- approved_by/at.

The platform never invents the threshold values; business owners supply them.

See ADR-0119 for the full proposal: required fields, component dependencies, storage and versioning options, and security.
