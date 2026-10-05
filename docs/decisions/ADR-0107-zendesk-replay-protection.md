# ADR-0107: Zendesk webhook replay protection

Status: Proposed — schema gap, approval needed · Date: 2026-09-29

## Gap
The webhook must be "signed/replay-protected". `zendesk_events` stores `hmac_verified` but has no:
- signature timestamp;
- Zendesk event or webhook id;
- uniqueness constraint.

Without these, the same signed payload can be replayed.

## Proposal
- Add `external_event_id` (unique) and `signed_at timestamptz` to `zendesk_events`.
- Reject payloads outside a 5-minute window, or whose id has already been seen.
- Reject before persisting anything unless the HMAC verifies. Unverified payloads are never stored as trusted.
- Needs the Zendesk signing secret and the payload contract from the integration owner.
