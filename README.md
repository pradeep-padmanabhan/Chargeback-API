# Chargeback Management Platform — .NET 9 backend

Single ECS-hosted API built with vertical slices, Carter endpoints and the MediatR pipeline. Start with [`docs/CLAUDE.md`](docs/CLAUDE.md) and [`docs/CHARGEBACK_FRONTEND_BACKEND_AI_COMMON.md`](docs/CHARGEBACK_FRONTEND_BACKEND_AI_COMMON.md).

**Status:**
- **Phases 0–4 (foundation):** complete.
- **Phase 5 (intake + ten-gate skeleton):** in progress. Portal intake, the gate registry and engine, and persistence are done. No gate criteria are implemented, because none are signed off, so every dispute is FLAGGED.
- **Phase 6 (scheme rules + issuer triage):** implemented.
  - Deterministic engines, a system-only triage step, and persistence with audit.
  - With production configuration, triage is always recorded as **Incomplete**, because no scheme settings (ADR-0122) or bank configuration (ADR-0119) are approved.
  - Synthetic fixtures demonstrate the mechanics in tests only.

- **Phase 7 (Case Management):** implemented.
  - A case is created for every dispute: FLAGGED cases go to the analyst queue, NEW cases are triaged automatically.
  - Case APIs with `If-Match` concurrency, a timeline, analyst status transitions, assignment and manual re-triage.
  - Idempotent outbox consumers.

A fresh database is the baseline plus every migration in order; migrations for existing development databases are in [`db/migrations/`](db/migrations/README.md).

## Layout

```
src/
  Chargeback.Api/             host, MediatR behaviors, security, Features/<slice>/ (endpoints + requests + handlers)
  Chargeback.SharedKernel/    Result, paging, entity/event bases, ICurrentUser, value objects, PAN redaction
  Chargeback.Infrastructure/  EF Core (baseline mapping), Dapper, outbox, security lookups, BedrockAiClient
tests/
  Chargeback.UnitTests/          value objects, behaviors, authorization, AI client
  Chargeback.ArchitectureTests/  layering, slice isolation, request declarations, no migrations
  Chargeback.ContractTests/      OpenAPI snapshot + endpoint rules (no DB)
  Chargeback.IntegrationTests/   Testcontainers PostgreSQL + baseline SQL; RBAC, bank isolation, tx/outbox
  Chargeback.TestSupport/        WebApplicationFactory + test auth scheme (test-only)
docs/
  decisions/   ADRs (accepted decisions and schema gaps)
  contracts/   openapi-v1.json, API conventions, events, AI capabilities
```

## Build and test

Requires the .NET SDK 9.0.3xx (pinned in `global.json`). Integration tests also require Docker.

```bash
dotnet build                                   # warnings are errors; NuGet audit enforced
dotnet test tests/Chargeback.UnitTests
dotnet test tests/Chargeback.ArchitectureTests
dotnet test tests/Chargeback.ContractTests
dotnet test tests/Chargeback.IntegrationTests  # starts postgres:16-alpine, loads docs/CHARGEBACK_DIAGRAM_BASELINE.sql
dotnet format --verify-no-changes
```

### Changing the API contract

The committed `docs/contracts/openapi-v1.json` is the contract. After an intentional change:

```bash
UPDATE_CONTRACT_SNAPSHOTS=true dotnet test tests/Chargeback.ContractTests
```

Commit the regenerated file together with the code change.

## Database

The schema is owned by [`docs/CHARGEBACK_DIAGRAM_BASELINE.sql`](docs/CHARGEBACK_DIAGRAM_BASELINE.sql). The application never runs migrations or DDL (ADR-0004).

- **Local development:** apply the script to a new, empty database only (`psql -v ON_ERROR_STOP=1 -f docs/CHARGEBACK_DIAGRAM_BASELINE.sql`).
- **Credentials:** set them with user secrets (`ConnectionStrings:Chargeback`), never in appsettings.

## Configuration

| Key | Purpose |
|---|---|
| `ConnectionStrings:Chargeback` | PostgreSQL |
| `Authentication:Cognito:{Region,UserPoolId,AllowedClientIds}` | Cognito access-token validation |
| `Outbox:DispatcherEnabled` | Background outbox delivery (on in Development) |
| `Outbox:Transport` | `Logging` (default) or `InProcess` (delivers to the case-creation and automatic-triage consumers; Development) |
| `Ai:*` | Feature flags, model id, timeout, retries (all off by default) |
| `Intake:Gates:ActiveGates` | Gate numbers whose evaluator may run (empty: every gate PENDING_DEFINITION) |
| `SchemeRules:*` | Calendar time zone, effective-date basis, clock-start basis, day counting (no defaults; ADR-0122) |
