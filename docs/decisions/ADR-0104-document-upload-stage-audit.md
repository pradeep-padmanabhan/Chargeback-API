# ADR-0104: Immutable document upload-stage audit

Status: Proposed — schema gap, approval needed · Date: 2026-09-29

## Gap
Common guide §8.5 and CLAUDE.md task 7 require "immutable upload-stage recording". `documents.scheme_stage` is a mutable column, and there is no history of stage or status changes.

## Options
1. An append-only `document_events` table (document_id, from/to stage, from/to status, actor, at).
2. Treat `scheme_stage` as write-once (enforced by a trigger or grants) and record status changes through `domain_events`.

Also blocked on the related question Q13: is the stage client-supplied at upload, or derived from the case's scheme state?

## Decision (2026-10-03): option 2, implemented (migration 0007)
- **Write-once upload stage.** A trigger on `documents` refuses changes to `case_id`, `file_name`, `s3_key`, `mime_type`, `file_size_bytes`, `scheme_stage`, `upload_source`, `uploaded_by` and `uploaded_at`. It also refuses:
  - moving `upload_status` from UPLOADED back to PENDING_UPLOAD;
  - changing `upload_confirmed_at` once set;
  - clearing a soft delete;
  - any `DELETE`.
- **Stage at upload (Q13 resolved).** The client declares `schemeStage` when it declares the upload, and the value is recorded immutably.
- **History.** Status changes go to the case timeline through `domain_events`: `document.upload.requested`, `document.uploaded`, `document.processed` and `document.deleted`.
- **Classification runs.** Each run is an append-only row in `document_classifications`.
- **Independent statuses.** `upload_status` (PENDING_UPLOAD → UPLOADED) is independent of the processing status (`document_status`: Pending → Processing → Success | Failed).
