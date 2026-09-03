# 55 — Events and Audit

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Two stores, deliberately

`events` records **what happened**. `audit_log` records **who was allowed to make it happen**.
They are physically separate tables with different retention (D-018). Merging them would force one
retention policy on two very different needs and would make the audit trail as noisy as the operational
history.

## Event contract

`event_id`, `event_type`, `aggregate_type`, `aggregate_id`, `aggregate_version`, `correlation_id`,
`causation_id`, `transition_id` where applicable, `payload_json`, `schema_version`, `occurred_at`, `seq`.

`UNIQUE(aggregate_type, aggregate_id, aggregate_version)` gives optimistic concurrency for free: a lost
update becomes a constraint violation at commit rather than silent overwriting.

## Append-only, enforced

There is no `UPDATE` or `DELETE` statement against `events` anywhere in the codebase, and a security test
asserts that by scanning the compiled SQL surface. Append-only maintained by convention is append-only
until someone is in a hurry.

## Audit contract

`audit_id`, `action`, `actor_type`, `actor_id`, `subject_type`, `subject_id`, `production_id`, `outcome`,
`policy_decision_id`, `reason_code`, `correlation_id`, `occurred_at`.

`actor_type` has no `AGENT` value (I-09). An agent is never the authority for a protected action, so it
can never be recorded as the actor for one — not even by a bug, because the enum has no such value.

## Coverage requirement

Every protected action writes both a `policy_decisions` row and an `audit_log` row. A protected action
found in testing with neither is a defect, and the test suite checks this by enumerating protected
actions and asserting a decision row for each.

## Replay

Given the event stream and the policy version history, the system's decisions are reproducible. This is
what makes an incident investigable rather than merely regrettable.
