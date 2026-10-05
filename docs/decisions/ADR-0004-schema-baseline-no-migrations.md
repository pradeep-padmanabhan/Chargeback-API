# ADR-0004: Schema baseline, no migrations, ephemeral test databases

Status: Accepted (Q1, Q2); amended Phase 7 · Date: 2026-09-29

## Decision

- `docs/CHARGEBACK_DIAGRAM_BASELINE.sql` (schema `chargeback_diagram`) is the only schema source. The expanded 110-feature package is not used or referenced.
- No EF migrations are generated or executed. `Microsoft.EntityFrameworkCore.Design` is not referenced, and the application never issues DDL. An architecture test fails if a migration type appears.
- EF maps every baseline table and column exactly: snake_case names, explicit FKs without navigations, the `xmin` concurrency token, and enum converters that match the CHECK constraints. A schema-conformance integration test compares the EF model with `information_schema`, column by column.
- Integration tests load the baseline SQL into a throwaway Testcontainers PostgreSQL 16 container for each run.
- Commands use EF Core. Reads use Dapper (`IDapperQueryService`), which joins the EF transaction when one is open.
- Ids are application-generated UUIDv7s. Timestamps are set from `TimeProvider`, because the schema has no triggers.

## Consequences

Resolving the schema gaps (ADR-0101 to ADR-0118) requires an approved revision of the baseline script and a decision on migration tooling.

## Amendment (Phase 7)
- **Structural changes live in versioned SQL migrations** under `db/migrations/`, as instructed. Examples are the `cases.status` CHECK constraint, the case reference sequence and `processed_domain_events`. The baseline carries approved permission *definitions* only.
- **A fresh install is therefore 0001 (baseline) plus every migration in order.** Integration tests build every database that way, and prove each migration is idempotent.
- The application still never runs migrations or DDL. Migration tooling remains an open decision.
