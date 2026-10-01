# ADR-0103: Triage rule version and inputs

Status: Proposed — schema gap; interim audit implemented in Phase 6 · Date: 2026-09-29

## Gap
Act 3 says to store `triage_results` "with inputs/rule version". The table has no reference to:
- the `scheme_rule_specs` row(s) applied;
- the bank configuration version;
- an inputs snapshot or hash.

Deterministic results cannot be reproduced or audited without these.

## Proposal
Add to `triage_results`:
- `rule_spec_id uuid` (FK to `scheme_rule_specs`);
- `bank_config_version`;
- `inputs_hash varchar(64)`;
- `inputs_snapshot jsonb`, holding masked data only.

## Interim implementation (Phase 6)
There are no columns for the audit data, so each triage run writes one `triage_results` row plus a `triage.completed` event. The event is written atomically, in the same save as the row. It is the durable audit record and carries:
- the rule spec id and reason-code id used;
- the evaluation date;
- the deadline inputs and result;
- the required documents;
- every candidate rule's match result;
- the exclusion counts: draft, retired, not yet effective, expired, reason code not effective;
- the bank-configuration version;
- per-component status.

The `triage_results` columns are used as follows (proposed):
- `triage_layer`: the layer that decided, `SCHEME_RULES` or `ISSUER_TRIAGE`.
- `routing_policy_outcome`: the matched routing rule id.
- `risk_flags`: the fired risk factor ids.
- `outcome`: NULL when the evaluation is incomplete.

Adding `rule_spec_id`, `bank_config_version` and `inputs_hash` columns is still recommended.
