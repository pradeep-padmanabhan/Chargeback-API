# ADR-0108: AI decision log scope, versions and overrides

Status: Proposed — schema gap, approval needed · Date: 2026-09-29

## Gap
Common guide §4 requires recording, per invocation:
- model/version;
- prompt/version;
- latency and tokens;
- the decision and any human override;
- bank/case scope.

`ai_decision_logs` lacks:
- `bank_id`: rows without a case, such as SDK or email intake, have no tenant scope;
- a prompt version column: the prompt version is currently stored combined as `prompt_template_id = "<id>@<version>"`;
- a result status or failure reason: currently embedded in `raw_response`;
- human override fields.

`cases.ai_summary` also has no link to the log row that produced it, so the summary's model and prompt version cannot be displayed.

## Proposal
- Add to `ai_decision_logs`: `bank_id`, `prompt_version`, `status`, `failure_reason`, `overridden_by`, `overridden_at` and `override_value jsonb`.
- Add `cases.ai_summary_log_id` (FK).

## Interim (2026-10-03): review summary provenance without a schema change
The Human Review workspace shows the stored `cases.ai_summary` with the model and prompt of the **first successful** `TriageSummary` row in `ai_decision_logs` for the case. This is correct because the summary is written only once, while `ai_summary` is empty. The proposed `cases.ai_summary_log_id` link would make it explicit. The other proposed columns are still open.
