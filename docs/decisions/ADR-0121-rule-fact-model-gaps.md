# ADR-0121: Facts available for deterministic rule matching

Status: **Proposed — schema gap, scheme SME input required** · Date: 2026-09-29

## Context
Rules can only use facts that are persisted. The fact catalogue (`FactCatalog`) is restricted to what the baseline `disputes` row provides, plus the scheme layer's result:

| Fact | Source |
|---|---|
| `dispute.intakeChannel` | `disputes.intake_channel` |
| `dispute.transactionAmount` | `disputes.transaction_amount` |
| `dispute.currencyCode` | `disputes.currency_code` |
| `dispute.transactionDate` | `disputes.transaction_date`, as a date in the configured calendar |
| `dispute.receivedDate` | `disputes.created_at`, as a date in the configured calendar |
| `dispute.merchantName` | `disputes.merchant_name` |
| `dispute.hasAcquirerReferenceNumber` | ARN present |
| `dispute.hasCardNumber` | masked card present |
| `scheme.reasonCode` | determined reason code (issuer layer only) |

## Gap
Deriving a scheme reason code normally depends on facts the baseline **does not capture**. Examples (for SME confirmation, not asserted as scheme rules):
- the cardholder's dispute claim or category (e.g. not recognised, not received, not as described, duplicate, credit not processed);
- merchant category code (MCC);
- transaction type or channel (card-present, card-not-present, recurring);
- region or inter-regional indicator;
- card product;
- authorization and clearing details;
- whether the cardholder contacted the merchant;
- the expected delivery date.

Without them, real reason-code rules cannot be expressed, and the engine will report `NoMatch` or `IncompleteFacts`. That is the correct, safe behaviour.

## Decisions required
- **SMEs:** the list of facts each approved rule needs.
- **Schema:** where those facts are stored. Options are new `disputes` columns, a typed `dispute_facts` table, or a validated `jsonb` column. Plus the changes to intake contracts, the SDK and email extraction to capture them.
- **Validation:** which facts must be verified (for example, by transaction lookup at Gate 2) rather than taken from the claimant.
