# ADR-0114: Client Portal notifications

Status: Proposed — schema gap, approval needed · Date: 2026-09-29

## Gap
The frontend guide lists notifications for the Bank Client Portal. The baseline has no notifications table and no read/unread state, so no notifications endpoint is in the contract yet.

## Needed
- The notification triggers.
- Whether notifications are derived from the case timeline (ADR-0109) or stored separately.
- Retention.
- Email/SES duplication rules.
