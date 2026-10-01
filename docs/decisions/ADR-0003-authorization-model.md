# ADR-0003: Authorization model — database permissions and explicit bank scope

Status: Accepted (Q5, Q6) · Date: 2026-09-29

## Decision

- Single Cognito user pool for processor and bank users. Only access tokens from configured app clients are accepted (`token_use = access`, `client_id` allow-list). Only the `sub` claim is trusted.
- User type, role, status, permissions and bank scopes are loaded from the database for each request (`users`, `roles`, `role_permissions`, `permissions`, `user_bank_scopes`). Cognito groups are not used.
- **Bank users:** scope is `{users.bank_id}` only; `user_bank_scopes` rows are ignored for them.
- **Processor and admin users:** scope is the currently valid `user_bank_scopes` rows only (`valid_from <= now < valid_until`).
- **A NULL `bank_id` never means all banks, for any user type.**
- Deny by default. Checks run in this order:
  1. The request's authorization declaration is valid.
  2. The user is resolved: 401 if unauthenticated; 403 if not provisioned, not `ACTIVE`, or role type ≠ user type.
  3. User-type restriction (403).
  4. Permission (403).
  5. Bank scope.
- A resource outside the caller's scope returns **404 `RESOURCE_NOT_FOUND`**, identical to a missing resource.
- Each request declares one scope rule:
  - `IBankScopedRequest`: the bank id is in the request.
  - `IResourceScopedRequest`: the owning bank is resolved from the dispute, case, document, filing or bank user.
  - `IScopeFilteredRequest`: the handler filters results to the caller's scopes.
  - `[NotBankScoped("justification")]`.

## Open points

- Should an inactive bank (`banks.status <> 'ACTIVE'`) remain accessible? It currently is.
- Who administers processor and admin users, who are not bank-owned?
