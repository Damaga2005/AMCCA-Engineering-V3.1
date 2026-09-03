# 73 — Concurrency Test Suite

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

| # | Test | Assertion |
|---|---|---|
| C-01 | N workers claim one `QUEUED` job simultaneously | Exactly one transitions it to `LEASED`; one `leases` row |
| C-02 | Worker paused past lease expiry, then attempts to commit | Fence token stale; write abandoned |
| C-03 | N concurrent reservations against a budget with capacity for N-1 | Exactly N-1 succeed; `reserved <= limit_amount` always |
| C-04 | Concurrent state transitions on one production | One succeeds; the other fails on `aggregate_version` and retries against current state |
| C-05 | Concurrent publication dispatch to the same target | One dispatches; the other is refused by lock or unique constraint |
| C-06 | Lock acquisition forcibly disabled; concurrent dispatch | Unique constraint prevents the duplicate; `AMCCA-PUB-008` |
| C-07 | Concurrent artifact version inserts for one artifact | `UNIQUE(artifact_id, version_no)` holds; no gap, no duplicate |
| C-08 | Concurrent event appends for one aggregate | `UNIQUE(aggregate_type, aggregate_id, aggregate_version)` holds |
| C-09 | Retention running while a rework references a superseded version | Nothing referenced is collected |
| C-10 | Reconciliation and a manual retry racing on one intent | Exactly one resolution recorded |
| C-11 | Every transaction in the suite measured against the wall-clock ceiling | No transaction exceeds it; no network call inside one (I-22) |
| C-12 | Scheduler dispatching while the kill switch engages | No work dispatched after the engage event commits |
| C-13 | Clock jumps backwards mid-run | Leases do not double-grant; fence tokens still order correctly |
| C-14 | SQLite `BUSY` under sustained write pressure | Retries within `busy_timeout`; `AMCCA-DB-003` beyond it; no corruption |

C-06 is the important one. It deliberately disables the first line of defence to verify that the last line
actually works. A guarantee that has only ever been observed while its backup was also present has not
been tested.
