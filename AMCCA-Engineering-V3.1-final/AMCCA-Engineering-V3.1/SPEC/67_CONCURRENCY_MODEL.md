# 67 — Concurrency Model

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## The four mechanisms

Everything concurrent in this system reduces to one of four mechanisms. If a new piece of concurrency
does not fit one of them, it needs a SPEC change.

### 1. Optimistic concurrency on aggregates

`aggregate_version` on `productions`, plus `UNIQUE(aggregate_type, aggregate_id, aggregate_version)` on
`events`. A concurrent modification fails at commit with a constraint violation and is retried by reading
the current state and re-applying — never by overwriting.

### 2. Single-statement conditional updates

Job claiming and budget reservation. The condition and the write are the same statement, so there is no
window between check and act:

```sql
UPDATE jobs SET state='LEASED' WHERE id=? AND state='QUEUED';
UPDATE budgets SET reserved=reserved+? WHERE id=? AND reserved+? <= limit_amount;
```

Read-then-write versions of either are forbidden and are covered by a concurrency test (`SPEC/73`).

### 3. Leases with fence tokens

A worker holds a lease with a monotonically increasing fence token. Expiry allows another worker to
claim. A worker whose token is stale MUST abandon its write. This handles the case where a worker was
paused — by a garbage collection pause, a VM suspend, a debugger — long enough for its lease to expire
while it still believes it holds one.

### 4. Named locks

The publication lock per `(production_id, platform, account_id)`. Advisory: it avoids the collision, and
the unique constraint guarantees the outcome if it fails.

## Transaction discipline

No transaction contains a network call, a media render or a user prompt (I-22). A concurrency test fails
any transaction exceeding a configured wall-clock ceiling. This is what keeps WAL contention bounded and
what makes the intent-before-effect ordering possible at all.

## SQLite specifics

WAL allows one writer and many readers. Writers are short. `busy_timeout` is configured so a brief
contention retries rather than failing. A `SQLITE_BUSY` beyond the timeout is `AMCCA-DB-003` and is
retryable.
