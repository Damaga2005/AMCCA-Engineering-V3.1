# 63 — Notifications

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Severities

| Severity | Meaning | Behaviour |
|---|---|---|
| `INFO` | Something completed | Auto-dismissible |
| `WARNING` | Attention advisable | Persists until acknowledged |
| `ERROR` | Something failed | Persists until acknowledged |
| `CRITICAL` | Action required or safety-relevant | **Never auto-dismissed**; persists across restart |

## Categories

Approval required, budget threshold, unknown external state, reconciliation exhausted, publication
verified, publication failed, rights problem, disclosure problem, security event, dead-letter, storage
low, provider degraded, capability expired.

## Rules

1. A `CRITICAL` notification is never auto-dismissed and survives restart, because the conditions that
   produce one — an unresolved unknown publication, a security event — do not resolve themselves.
2. Every notification names the affected entity and links to it.
3. Notifications are deduplicated by `(category, subject_id)` with a count, so a repeated condition
   produces one item with a count rather than a hundred items.
4. A notification is never the only record of an event. It is a pointer to the event and audit rows.

Rule 4 matters: notifications are an operator convenience with their own retention. The durable record
lives in `events` and `audit_log`.

## What does not produce a notification

Routine job completion, normal state transitions, successful retries within bounds, and scheduled
maintenance. Notification fatigue destroys the value of the `CRITICAL` severity, and the `CRITICAL`
severity is the one that has to work.
