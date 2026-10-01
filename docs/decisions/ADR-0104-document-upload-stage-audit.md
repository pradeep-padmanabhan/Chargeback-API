# ADR-0104: Immutable document upload-stage audit

Status: Proposed — schema gap, approval needed · Date: 2026-09-29

## Gap
Common guide §8.5 and CLAUDE.md task 7 require "immutable upload-stage recording". `documents.scheme_stage` is a mutable column, and there is no history of stage or status changes.

## Options
1. An append-only `document_events` table (document_id, from/to stage, from/to status, actor, at).
2. Treat `scheme_stage` as write-once (enforced by a trigger or grants) and record status changes through `domain_events`.

Also blocked on the related question Q13: is the stage client-supplied at upload, or derived from the case's scheme state?
