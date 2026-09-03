# Failure, Recovery, Cost and Storage

## Error classes

`TRANSIENT`, `RATE_LIMITED`, `AUTH`, `CONFIGURATION`, `VALIDATION`, `PROVIDER`, `PLATFORM`, `MEDIA`,
`RIGHTS`, `COMPLIANCE`, `POLICY`, `BUDGET`, `STORAGE`, `SECURITY`, `USER_ACTION_REQUIRED`,
`UNKNOWN_EXTERNAL_STATE`, `INTERNAL`.

Every class has a defined retry disposition and a catalogued code range in `SPEC/05`. Only errors marked
retryable by the adapter *and* permitted by policy may retry, with bounded attempts and bounded
cumulative retry cost. A retry that costs more than the original operation is not resilience.

## Recovery on startup

The recovery pass runs **before** the scheduler starts, and looks for:

- jobs with expired leases (recoverable after validating the fence token)
- incomplete artifact writes (temp files without a committed version row)
- `intents` in `DISPATCHED` or `UNKNOWN` with no terminal evidence
- publications in non-terminal states with stale evidence
- analytics syncs interrupted mid-window
- an absent clean-shutdown marker, which escalates the depth of the pass

## Reconciliation

Each adapter implements a status lookup where the external system supports one. Reconciliation is both
periodic and event-driven after any uncertain operation. It is bounded by `policy.reconcile.max_attempts`;
on exhaustion the production moves to `BLOCKED` and an operator is notified. It never guesses.

A publication is `VERIFIED` only from authoritative platform evidence. An upload accepted for processing
is not published. A video visible in a creator dashboard is not necessarily public.

## Cost control

Reservation and settlement are separate events. Before expensive work, budget is atomically reserved by a
single conditional `UPDATE` whose limit check lives in the `WHERE` clause, so two workers cannot both
pass it. Completion settles the actual cost and releases the remainder. Unsettled reservations expire.

Cost is estimated from an immutable `pricing_snapshots` row that carries `retrieved_at` and `source_ref`.
Actual usage is reconciled from provider request identifiers where available. If usage cannot be
reconciled, cost stays `ESTIMATED_UNRECONCILED` and the budget stays conservatively reserved. An
unreconciled cost is a known unknown, not a zero.

Budget windows: per-production, per-rework, per-recovery, daily and monthly. `SPEC/20` defines their
precedence and the consistency rule the preflight enforces, because a daily cap that can exceed the
monthly cap is a configuration bug the system should refuse to start with.

## Storage lifecycle

Minimum free space is checked before any render is scheduled; a render that would breach it is not
dispatched rather than failing halfway. Temp, cache, superseded artifacts and final artifacts have
separate retention clocks (`SPEC/52`). Collection respects active DAG references, pending reconciliation
and legal or rights holds. Deleted artifacts leave a tombstone so that history remains reconstructable.
