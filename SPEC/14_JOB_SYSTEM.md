# 14 — Job System

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Fields

See `job.schema.json` and the `jobs` table. Every job has `id`, `type`, `state`, `priority`,
`idempotency_key`, `attempt`, `max_attempts`, `correlation_id` and a payload.

## Priorities

| P | Class | Examples |
|---|---|---|
| 0 | Emergency and control | Kill switch propagation, reconciliation of an unknown publication |
| 1 | Publication and verification | Publish, poll status, verify |
| 2 | Operator requested | Anything the operator asked for directly |
| 3 | Scheduled production | The autonomous pipeline |
| 4 | Analytics | Metric ingestion |
| 5 | Discovery | Signal and trend gathering |

Aging raises effective priority over time so that P5 work is not starved indefinitely by a busy pipeline.
Reconciliation is P0 rather than P1 because an unresolved ambiguity blocks correctness, not just progress.

## Leasing

A worker claims a job with a single conditional statement:

```sql
UPDATE jobs SET state='LEASED' WHERE id=? AND state='QUEUED';
```

paired with an insert into `leases` carrying a monotonically increasing `fence_token`.
Read-then-write claiming is forbidden. Heartbeats extend `lease_until`. An expired lease becomes
recoverable only after validating that the previous owner's fence token is stale; a worker whose token is
stale MUST abandon its write rather than complete it, and does so via `AMCCA-JOB-001` (TRANSIENT,
retryable by whoever now holds the lease -- this is an expected race outcome, not an operator problem).

## Concurrency limits

A global worker cap, plus per-provider and per-platform caps by rate class. The scheduler never dispatches
work whose reserved budget is unavailable or whose disk requirement exceeds free space — a render that
cannot finish is not started.

## Retries and dead-lettering

Bounded by `max_attempts` and by cumulative retry cost. On exhaustion the job moves to `DEAD_LETTER`. It
carries `AMCCA-JOB-003` (SPEC/60 obligation 6) as soon as an operator looks at it in Job Queue -- the
Reason Code column is computed from `state = DEAD_LETTER`, not a separate stored fact that could drift
from it. A dead-lettered job is never silently dropped and never automatically retried; it waits for an
operator, who discovers it by its state in that screen. There is no push notification: JobManager has no
dependency capable of raising one (Core does not depend on the WPF notification service, and no
job-lifecycle audit trail exists yet to drive one another way), so an operator who is not looking at Job
Queue will not be alerted the moment a job dead-letters.

An operator requeuing a `DEAD_LETTER` job does **not** reset its `attempt` counter. Zeroing it would erase
both the `max_attempts` bound and the retry history, letting a poisoned job loop indefinitely with no
record. Preserving it grants exactly one further attempt, after which the job returns to `DEAD_LETTER` for
the operator to look at again. This is a bounded retry, not a fresh budget — an operator who wants a job to
survive more than one further attempt raises `max_attempts` explicitly rather than requeuing repeatedly.

## What a job may not do

Hold a database transaction across its whole execution. Perform an `EXTERNAL_UNSAFE` call without a
committed intent. Extend its own lease indefinitely without heartbeating. Modify a production's state
directly rather than through the Orchestrator.
