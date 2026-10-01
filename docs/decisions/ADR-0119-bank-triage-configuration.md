# ADR-0119: Bank-specific triage configuration

Status: **Proposed — product owner, bank SMEs and security approval required** · Date: 2026-09-29
Supersedes the placeholder in ADR-0105.

## Context
The Issuer Triage Engine (common guide §6 Act 3.2) evaluates four components against **approved bank configuration**:
- Hard Eligibility
- Routing Policy
- Risk Scoring
- Human Review Trigger

The baseline schema (`CHARGEBACK_DIAGRAM_BASELINE.sql`) has no table, column or document for bank-specific configuration or thresholds. Per the Phase 6 instruction, no table, schema, default threshold or business rule has been invented.

## What is implemented now (Phase 6)
- **In-memory model:** `BankTriageConfiguration`, shown below. It has no persistence.
- **Provider interface:** `IBankTriageConfigurationProvider.GetApprovedAsync(bankId, asOf)`. It returns `Approved`, `NotConfigured` or `Unavailable`.
- **Production default:** `PendingApprovalBankTriageConfigurationProvider` always returns `NotConfigured`. Every triage is therefore recorded as **Incomplete**, with no outcome and `human_review_triggered = true`.
- **Engine logic:** tested with **synthetic** configurations only.

## Required configuration fields and dependent components

| Field | Type | Used by | Notes |
|---|---|---|---|
| `version` | string | all (audit) | Recorded in the `triage.completed` event |
| `hardEligibility[]` — `id`, `condition`, `outcomeWhenNotMet` | rule list | Hard Eligibility | A rule that evaluates false decides its configured outcome (e.g. `Invalid`, `SendToCompliance`) |
| `riskScoring.factors[]` — `id`, `condition`, `weight` | factor list | Risk Scoring | Score = sum of the weights of true factors; fits `triage_results.risk_score numeric(8,4)` |
| `riskScoring.humanReviewThreshold` | decimal | Risk Scoring → Human Review Trigger | score ≥ threshold → `RouteToHuman` |
| `humanReviewTriggers[]` — `id`, `condition`, `reason` | rule list | Human Review Trigger | Any true trigger → `RouteToHuman` |
| `routingPolicy[]` — `id`, `condition`, `outcome` (ordered) | rule list | Routing Policy | First true rule decides the outcome (`ProceedToFiling`, `AutoRefund`, `SendToCompliance`, `Defer`, …). No match → incomplete |

Conditions use the proposed rule condition format (ADR-0120) over the fact catalogue (ADR-0121). Rule ids are unique and at most 80 characters, because `triage_results.routing_policy_outcome` is `varchar(80)`.

### Proposed evaluation order (needs confirmation)
1. The scheme layer must be Determined; otherwise triage is incomplete.
2. Hard eligibility: the first rule not met decides its outcome.
3. Risk threshold or any human-review trigger decides `RouteToHuman`.
4. Routing policy: the first match decides.

Any unknown fact, missing configuration, invalid configuration or unmatched routing makes the triage **Incomplete** (outcome NULL) and routes it for manual attention. An unreadable configuration store makes it **Unavailable**: nothing is recorded, and the caller retries later.

## Storage and versioning options
1. **Table `bank_triage_configurations`** with these columns. **Recommended.**
   - `id`, `bank_id` (FK), `version`, `config jsonb`
   - `approval_status` (DRAFT/APPROVED/RETIRED), `effective_from`/`effective_to`
   - `created_by`/`at`, `approved_by`/`at`
   - A unique constraint preventing two APPROVED rows effective at once for the same bank.

   This mirrors the lifecycle of `scheme_rule_specs`.
2. **Normalized tables** (one per component). Stronger typing, but more schema to change for every new field.
3. **External configuration service or AWS AppConfig.** Keeps the database schema small, but it is harder to audit, version per bank, and join to cases.

In every option, triage records the configuration id and version it used. Today that is only possible in the event, because there is no column (see ADR-0103).

## Security and bank isolation
- **Bank isolation:** configuration is strictly per bank. A provider must never return another bank's configuration or a platform-wide default. With RLS (ADR-0006), the configuration table is bank-scoped.
- **Who may change it:** only authorized processor/admin users with a new permission (proposed `MANAGE_BANK_TRIAGE_CONFIG`) and scope for that bank.
- **Maker-checker approval:** the creator cannot approve their own configuration. This is proposed and needs a decision.
- **Audit:** every configuration change is audited, and it is immutable once APPROVED; changes create new versions.
- **Bank-user visibility:** bank users may not read other banks' configuration. Whether they may read their own is a product decision.
- **Content:** configuration holds no card data or personal data.

## Decisions required
1. Approve the storage option and the schema change.
2. Approve the field list and evaluation order above. Or supply the bank's actual policy structure, if it differs.
3. Confirm how each hard-eligibility failure maps to an outcome.
4. Confirm the risk scoring method (additive weights) and each bank's thresholds.
5. Confirm the approval workflow (maker-checker) and the new permission name.
6. Decide the behaviour when **no routing rule matches**. Currently incomplete and routed for manual attention.
7. Supply per-bank values from bank SMEs.
