# ADR-0001: Solution structure and vertical-slice conventions

Status: Accepted (Phase 0–4 approval) · Date: 2026-09-29

## Decision

- One deployable, `Chargeback.Api` (ECS Fargate), hosts all slices. Slices are folders, not services.
- `Chargeback.SharedKernel` is dependency-free and holds genuinely common types only: `Result`, `PagedResult`, entity/event bases, `ICurrentUser`, value objects (`Money`, `CurrencyCode`, `CardMasked`, `ReasonCode`, `GateResult`, `TriageOutcome`, `DocumentStage`, `DocumentStatus`) and `PanRedactor`.
- `Chargeback.Infrastructure` holds the EF Core context mapped to the baseline, the Dapper query service, outbox, security lookups, the AI client and external adapters. It must not reference the Api.
- Slice layout: `Features/<Slice>/Contracts/*` (public DTOs — the only thing other slices may reference) and use-case files containing request + handler + endpoint mapping.
- Every MediatR request returns `Result`/`Result<T>` and declares exactly one permission rule and one scope rule (ADR-0003).
- Pipeline order follows the diagram: Logging → Validation → Authorization → Transaction → handler.
- Architecture tests enforce all of the above.

## Consequences

Phase 4 stubs live in one file per slice; from Phase 5 each use case gets its own folder.
