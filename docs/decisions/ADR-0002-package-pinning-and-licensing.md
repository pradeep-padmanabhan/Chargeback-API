# ADR-0002: Package pinning and licensing

Status: Accepted (Q10) · Date: 2026-09-29

## Decision

| Package | Version | Reason |
|---|---|---|
| MediatR | 12.5.0 | Last Apache-2.0 line; 13+ is commercially licensed |
| FluentAssertions | 7.2.2 | Last Apache-2.0 line; 8+ is commercially licensed |
| FluentValidation | 11.12.0 | Diagram specifies v11; Carter 9 depends on 11.x |
| Carter | 9.0.0 | MIT; targets net9.0 |
| ASP.NET Core, EF Core, Npgsql EF | 9.0.x | .NET 9 line |
| AutoMapper | not used | Commercial licence; mapping is explicit |

Central package management (`Directory.Packages.props`) with transitive pinning. NuGet audit (mode `all`, level `low`) fails the build. Dependabot ignores the licence-restricted major versions.

## Note

MediatR 12.5's `RequestHandlerDelegate` takes an optional `CancellationToken`; behaviors pass it through.
