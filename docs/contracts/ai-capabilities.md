# AI capability contracts (proposal for AI-team review)

Source: `src/Chargeback.Infrastructure/Ai/Capabilities/AiCapabilityContracts.cs`. There are three logical agents and six capabilities (common guide §4). Every call goes through `IBedrockAiClient`.

## Guarantees of `BedrockAiClient` (implemented and unit-tested)

| Guarantee | Behaviour |
|---|---|
| Feature flags | `Ai:Enabled`, `Ai:EnabledCapabilities[]` and `Ai:DisabledBankIds[]`. Off by default; a disabled capability never calls the model. |
| PAN masking | Masked to the last four digits before model input, and again in the stored raw response. |
| Temperature | Always 0. |
| Timeout and retry | Bounded timeout (`Ai:Timeout`) and bounded retries (`Ai:MaxAttempts`) on timeout or transient errors. No retry when the transport is not configured. |
| Strict output parsing | Unknown members are rejected: a model that adds e.g. `reasonCode` fails validation. Missing required members are rejected. An optional capability validator also runs. |
| Audit | Every invocation writes `ai_decision_logs` on its own connection, recording model, `prompt_template_id@version`, input hash, masked raw response, parsed output, latency and tokens. If the audit write fails, the output is discarded. |
| Fail-soft | Returns a status (`Success`, `Disabled`, `Unavailable`, `InvalidOutput`) and never throws into the workflow. |
| Transactions | Throws if called inside a database transaction; AI calls happen after commit. |

**Authority:** no output type can express a reason-code choice, a state change, a filing or a refund. AI output is advisory only.

## Capabilities

| Agent | Capability | Interface | Input (masked) | Output (advisory) |
|---|---|---|---|---|
| Extraction | Email Parser | `IEmailParser` | bank, subject, body, attachment names | candidate intake fields, each with confidence |
| Extraction | SDK Intake Guide | `ISdkIntakeGuide` | bank, session, history, cardholder message | next assistant message + draft fields |
| Classification | Attachment Matcher | `IAttachmentMatcher` | attachments (name, text excerpt), case slots | suggested slot per attachment + confidence |
| Classification | Document Verification | `IDocumentVerifier` | document, expected type, stage, extracted text | suggested type, confidence, concerns |
| Analysis & Explanation | Triage Summary | `ITriageSummarizer` | **existing** deterministic code, deadline, outcome, gates, required docs | summary text |
| Analysis & Explanation | Evidence Analysis | `IEvidenceAnalyzer` | reason code, documents (type, excerpt) | strength assessment, gaps, rationale |

## Current state
- All six are registered as fail-soft `Unavailable` implementations. There is no Bedrock transport and no prompts yet, pending Q11.
- Still needed:
  - the approved model id and region;
  - agreement on who owns prompt templates and evaluation;
  - an agreed confidence threshold for "low confidence → human review";
  - the ADR-0108 log fields.
