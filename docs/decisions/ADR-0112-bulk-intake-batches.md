# ADR-0112: Bulk intake batch tracking

Status: Proposed — schema gap, approval needed · Date: 2026-09-29

## Gap
CSV/Excel bulk intake needs a dry run followed by an upload. Nothing in the baseline stores:
- a batch;
- its source file (S3 key);
- a dry-run report or per-row errors;
- the link from resulting disputes back to their batch and row.

## Proposal
- Tables `intake_batches` and `intake_batch_rows`, or a `batch_id` on `disputes` plus S3-stored reports.
- Needs the approved CSV/Excel column template, which is not yet defined.
