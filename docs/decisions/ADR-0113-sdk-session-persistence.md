# ADR-0113: SDK session persistence

Status: Proposed — schema gap, approval needed · Date: 2026-09-29

## Gap
The SDK's conversational intake needs somewhere to keep:
- the session: bank, cardholder reference, expiry;
- the conversation turns (masked);
- the structured draft;
- used host-token `jti` values, for replay protection.

The baseline has no table for any of these.

## Options
- Tables `sdk_sessions` and `sdk_session_turns` with a short retention period.
- Or an external session store (Phase 1 has no Redis).

This depends on ADR-0005.
