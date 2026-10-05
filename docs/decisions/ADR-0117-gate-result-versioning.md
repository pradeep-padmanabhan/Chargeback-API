# ADR-0117: Gate result history and registry version

Status: **Behaviour approved 2026-09-29**; schema items (history, version column) still proposed · Date: 2026-09-29

## Gap
- `gate_results` is `UNIQUE(dispute_id, gate_number)`. If a flagged dispute is corrected and re-evaluated, earlier results must be overwritten, which loses the audit trail.
- No column records the gate registry or definition version used.
- `passed` is nullable. The proposed meaning of NULL is "no decision — definition pending sign-off", which leaves the dispute FLAGGED. This needs confirmation.

## Proposal
- Add `evaluation_run` (or `evaluated_at` in the unique key) and `gate_definition_version`.
- Confirm the NULL semantics.
- Confirm whether gates stop at the first failure or all run.

## Implemented in Phase 5 (provisional, pending confirmation)
- **Registry:** `ApprovedGateRegistry` holds the ten ER-diagram gates in order, at version `erd-baseline-1.2`. There are no evaluators yet.
- **Gates without an approved evaluator:** recorded as `passed = NULL` with reason `PENDING_DEFINITION: …`, so the dispute is **FLAGGED**.
- **Evaluator exceptions:** recorded as `passed = NULL` with reason `GATE_ERROR: …`, also FLAGGED. The dispute is never lost.
- **Execution:** all gates run even after a failure, so the analyst sees the complete trail.
- **Version storage:** the registry version travels in the `dispute.gates.evaluated` event only, because there is no column for it.
- **Re-evaluation:** not supported, because of the unique constraint.

These choices keep every dispute in human review until criteria are approved. Change them if the business decides otherwise.

## Decision (2026-09-29, approved)
- All ten gates execute in the approved order. Later gates still run after a failure, where they can run safely.
- A gate without approved criteria returns `passed = null` with reason `PENDING_DEFINITION`. Registered evaluators also stay pending until they are activated in `Intake:Gates:ActiveGates`.
- A gate with a technical error returns `passed = null` with the distinct reason `GATE_ERROR`. Evaluators may also return `GateOutcome.TechnicalError`.
- **Execution status:** exposed as `executionStatus` (`Passed`, `Failed`, `PendingDefinition`, `TechnicalError`) and in the `dispute.gates.evaluated` event (`pendingDefinitionGates`, `technicalErrorGates`). There is no status column, so it is derived from `passed` plus the reason prefix. An unrecognised undecided reason is reported as `TechnicalError`, never as passed.
- NEW only when all ten gates pass; otherwise FLAGGED. An undecided or errored gate never counts as a pass.
- Gate criteria remain unapproved, so no evaluators are registered.
- **Still open:** re-evaluation history, and a `gate_definition_version` column.
