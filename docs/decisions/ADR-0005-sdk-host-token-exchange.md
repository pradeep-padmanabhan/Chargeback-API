# ADR-0005: SDK authentication via host-app token exchange

Status: **Proposed — confirmation requested before implementation (Q6)** · Date: 2026-09-29

## Context

The embedded SDK runs inside the bank's app and must not inherit a Client Portal session. The chosen direction (Q6) is host-app token exchange: the bank issues a signed token and the backend validates it.

## Proposal (to confirm)

1. **Token:** a JWS (JWT) signed by the bank with an asymmetric key (ES256 or RS256). HMAC shared secrets are rejected.
2. **Key registration:** each bank registers a JWKS URL or public keys through Admin. Keys are pinned per bank by `kid` and rotated with overlap. *This needs a new table or config store, which is a schema gap and has not been created.*
3. **Required claims:**
   - `iss`: the registered bank issuer.
   - `aud`: the platform SDK audience.
   - `sub`: the bank's opaque cardholder reference (never a PAN).
   - `exp`: at most 5 minutes after `iat`.
   - `jti`: unique, and replay-protected.
   - Optionally, the card's last four digits.
4. **Exchange:** the SDK calls `POST /api/v1/sdk/sessions` with the host token. The backend validates:
   - the signature;
   - the issuer-to-bank mapping;
   - the audience;
   - the lifetime;
   - `jti` uniqueness.

   It then returns a short-lived, platform-signed SDK session token scoped to one bank and one cardholder reference.
5. **Principal:** a separate SDK principal type, not a platform user. It has no permissions beyond its own session and may create only one draft/dispute for its bank, with channel `SDK`.
6. **Transport:** TLS, plus optional mTLS between the bank backend and the platform if the bank proxies calls.

## Current state

The SDK routes exist in the contract, behind an authentication scheme that authenticates nobody, so every call returns 401.

## Questions

- How are bank keys registered?
- How long should host tokens and SDK sessions live?
- Do bank backends or mobile clients call the platform directly?
- Is a session store approved (ADR-0113)?
