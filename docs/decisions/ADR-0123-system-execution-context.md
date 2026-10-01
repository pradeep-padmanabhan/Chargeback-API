# ADR-0123: System execution context for workflow steps

Status: **Proposed (implemented for triage) — security review requested** · Date: 2026-09-29

## Context
Triage (Act 3) is a workflow step that follows intake. It is not something a user performs. The authorization pipeline (ADR-0003) only knows platform users, and a user's bank scope must not decide whether the workflow runs. Workers in Phase 7 and later (outbox consumers, pollers) need the same capability.

## Decision (implemented)
- `[SystemOperation("justification")]` is a third permission rule, alongside `[RequirePermission]` and `[AllowAnyPlatformUser]`. It cannot be combined with user-type restrictions.
- `AuthorizationBehavior` allows a system operation only while `SystemExecution.IsActive`, an in-process `AsyncLocal` scope. Otherwise the request fails with `403 SYSTEM_OPERATION_ONLY`, even for an admin holding every permission. No user is loaded.
- Bank scope is not applied to system operations: the workflow acts on the one case it was given. Results stay attached to that case's bank.
- **Architecture tests enforce two rules:**
  - only the `Chargeback.Api.Workers` namespace may reference `SystemExecution`; `AuthorizationBehavior` may only read it;
  - no Carter endpoint module may reference a system-operation request.
- Today only the integration tests enter system execution. Production invocation arrives with the Phase 7 case-creation workflow.

## Risks and mitigations
- **Misuse from HTTP code:** blocked by the architecture tests and code review. Because `AsyncLocal` does not flow out of an async method, elevation cannot leak back to a caller.
- **Workers bypassing RLS:** they must use an explicit system database scope (ADR-0006), not a user's.

## Decisions required
- Security sign-off on the model.
- Whether workers need a named service identity in the audit log. Currently the operation name is logged through the event and correlation id.
