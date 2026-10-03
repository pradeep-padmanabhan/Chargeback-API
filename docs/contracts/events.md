# Event contract (v1)

Domain events are written to the `domain_events` outbox **in the same database transaction** as the state change. The outbox dispatcher then publishes them to SNS FIFO. Delivery is **at-least-once**, so consumers must de-duplicate on `eventId`.

## Envelope
Stored verbatim in `domain_events.event_data` and published as the message body:

```json
{
  "eventId": "0192…",          // = domain_events.id (UUIDv7); SNS deduplication id; consumer de-dup key
  "eventType": "case.created",  // stable dotted name
  "schemaVersion": 1,           // incremented on breaking change of `data`
  "occurredAt": "2026-09-29T10:15:00Z",
  "correlationId": "…",         // from the originating request
  "bankId": "…",                // owning bank (tenant); null only for global configuration events
  "caseId": "…",                // when applicable; also the FIFO message group id
  "data": { }                   // event-specific; masked card data only
}
```

## Publishing rules
- FIFO `MessageGroupId` = `caseId` (or `eventId` when there is no case), which preserves per-case ordering.
- `MessageDeduplicationId` = `eventId`.
- The dispatcher claims rows with `FOR UPDATE SKIP LOCKED`, so it is safe to run several ECS tasks. It stamps `published_at` after publishing.
- Phase 4 ships the dispatcher disabled (`Outbox:DispatcherEnabled=false`) with a logging publisher. SNS topics and ARNs are not yet provisioned or approved.

## Planned catalogue (proposal; `data` schemas are defined per phase)

| eventType | Raised by | Phase |
|---|---|---|
| `dispute.received` | Intake | 5 |
| `dispute.gates.evaluated` | Intake | 5 |
| `dispute.flagged` | Intake | 5 |
| `case.created` | Case Management | 6/7 |
| `triage.completed` | Triage & Rules | 6 |
| `document.uploaded` | Evidence & Documents | 8 |
| `document.processed` / `document.failed` | Evidence & Documents | 8 |
| `review.entered` | Human Review | 9 |
| `review.decided` | Human Review | 9 |
| `filing.confirmed` | Network Filing | 10 |
| `filing.submitted` | Network Filing | 10 |
| `scheme.stage.changed` | Scheme Lifecycle | 10 |
| `portal.message.posted` | Client Portal & Comms | 11 |

Consumer de-duplication storage is an open schema gap (ADR-0106).

## Implemented `data` schemas (schemaVersion 1)

Every `data` object also repeats the envelope's base fields (`eventId`, `eventType`, `schemaVersion`, `occurredAt`, `bankId`, `caseId`). Event data never contains card or cardholder fields.

| eventType | Additional `data` fields |
|---|---|
| `dispute.received` | `channel` (`Portal`, `Email`, `Bulk`, `Api` or `Sdk`) |
| `dispute.gates.evaluated` | `disputeId`, `registryVersion`, `status` (`NEW` or `FLAGGED`), `passedGates[]`, `failedGates[]`, `undecidedGates[]`, `pendingDefinitionGates[]`, `technicalErrorGates[]` (gate numbers) |
| `dispute.flagged` | `disputeId` |
| `triage.completed` | `disputeId`, `triageResultId`, `status` (`Complete` or `Incomplete`), `outcome` (or null), `decidingLayer`, `detail`, `trigger` (`type` Automatic or Manual, `requestedBy`, `reason`, `sourceEventId`, `consumer`). Also a `scheme` object and an `issuer` object, described below. |
| `case.created` | `disputeId`, `caseReference`, `status` (`NEW` or `FLAGGED`), `sourceEventId` |
| `case.status.changed` | `action`, `fromStatus`, `toStatus`, `reason` (null when no rationale was given), `changedBy` |
| `case.assigned` | `fromUserId`, `toUserId`, `assignedBy` |
| `case.retriage.requested` | `requestedBy`, `reason`, `caseStatus` |
| `document.upload.requested` | `documentId`, `documentSlotId`, `mimeType`, `fileSizeBytes`, `schemeStage`, `requestedBy` |
| `document.uploaded` | `documentId`, `documentSlotId`, `schemeStage`, `confirmedBy` (consumed by `document-classification`) |
| `document.processed` | `documentId`, `classificationId`, `processingStatus` (Success/Failed), `failureReason` |
| `document.deleted` | `documentId`, `deletedBy` |
| `user.invited` | `userId`, `roleId`, `invitedBy` (no invite token) |
| `user.updated` | `userId`, `changedFields` (`fullName`, `status`), `fromStatus`, `toStatus`, `updatedBy` |
| `user.role.changed` | `userId`, `fromRoleId`, `toRoleId`, `changedBy` (immutable audit of every role change) |
| `user.deleted` | `userId`, `deletedBy` |
| `case.review.decided` | `decisionId`, `decision` (APPROVED/REJECTED), `reasonCodeId` (null on reject), `rationale`, `reviewedBy` |

The `triage.completed` sub-objects:

- **`scheme`:** `status`, `detail`, `evaluationDate`, `ruleSpecId`, `reasonCodeId`, `reasonCode`, `deadlineStatus`, `clockStartDate`, `filingDeadlineDate`, `timeLimitDays`, `requiredDocuments[]`, `candidates[]` (each `ruleSpecId`, `reasonCode`, `scenario`, `result`, `detail`) and `exclusions`.
- **`issuer`:** `configurationVersion` and `components[]` (each `component`, `status`, `detail`).

`triage.completed` is the triage audit record until ADR-0103 adds dedicated columns.

## Consumers (Phase 7)

| Consumer | Consumes | Effect | Idempotency |
|---|---|---|---|
| `case-creation` | `dispute.gates.evaluated` | Creates the case (NEW or FLAGGED) and raises `case.created` | `processed_domain_events` plus `cases.dispute_id` UNIQUE |
| `automatic-triage` | `case.created` with status NEW | Runs triage (system-only) and raises `triage.completed` | `processed_domain_events` |

`Outbox:Transport` is `Logging` by default. `InProcess` delivers events to these consumers and is used in development and tests. Timeline order within a save follows the order the events were raised (ADR-0109).
