# 52 — Data Retention

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Classes and default clocks

| Class | Default | Rationale |
|---|---|---|
| Temp files | Hours, configurable | Re-derivable; deleted on completion or recovery |
| Cache | Days | Re-derivable |
| Superseded artifact versions | Weeks | Needed while a rework cycle may still reference them |
| Final artifacts | Long, operator-controlled | The product of the system |
| Retrieved source documents | Medium | Needed to substantiate a claim; not needed indefinitely |
| Personal-data-flagged records | **Shortest** | `SPEC/51` minimisation |
| Events | Long | Operational history |
| Audit log | **Longest** | Accountability outlives operations |
| Cost and revenue records | Long, per accounting need | Financial records |
| Logs | Configurable, default short | Redacted, but still the least valuable data held |

The audit log outliving the event log is deliberate: reconstructing *who was allowed to do what* remains
valuable long after reconstructing *what the job queue was doing* stops being.

## Holds

Collection is suppressed by any of: a live DAG reference, an unsealed manifest, a pending reconciliation,
an open dispute on a cost or revenue record, or an explicit operator or legal hold. A held item is never
collected regardless of age, and the hold reason is visible.

## Deletion semantics

Deletion of an artifact writes a tombstone (I-08): the version row survives with `state = TOMBSTONED` and
its metadata and hash intact. History remains reconstructable while the bytes are gone. Deletion of a
personal-data record removes the content and retains only the fact that a record existed and was deleted,
with its timestamp.

## Execution

Retention runs as a low-priority scheduled job, is idempotent, is bounded per run so it cannot monopolise
IO, and logs what it collected in aggregate. It never deletes anything in the same run in which it first
observes it — a two-pass approach so that a clock anomaly or a mis-set configuration does not destroy data
in a single sweep.
