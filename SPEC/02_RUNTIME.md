# 02 — Runtime and Process Model

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Processes

One host process, `AMCCA.exe`, containing the UI thread, application services, orchestrator, policy
engine, scheduler, worker supervisor, agent runtime, adapters and the persistence layer.
FFmpeg runs as short-lived isolated child processes. No other process is required (D-011).

## Threading

- The UI thread never performs I/O, database access or waiting. All work is dispatched.
- Workers are managed background tasks with a bounded pool and per-provider concurrency caps.
- SQLite access uses short-lived connections with a configured `busy_timeout`. Connections are not shared across threads.
- Every long-running operation accepts a `CancellationToken` honoured within the configured grace period.

## Startup

1. Parse and schema-validate configuration (`SCHEMAS/config.schema.json`). Invalid config aborts with `AMCCA-CFG-001`.
2. Reject any literal credential found in configuration with `AMCCA-SEC-002`. This is an abort, not a warning.
3. Open the database; assert WAL and `foreign_keys=ON`; abort if either is off.
4. Take a `PRE_MIGRATION` backup and verify it opens read-only.
5. Apply migrations in order, verifying each recorded checksum against the shipped file.
6. Load `kill_switch_state`. If `EMERGENCY_STOP`, start in a halted UI-only mode.
7. Run preflight (`SPEC/49`).
8. Run recovery (`SPEC/16`).
9. Start the scheduler.

**Steps 8 and 9 are in that order and the order is normative.** Starting new work before reconciling old
ambiguity is the mechanism by which a crashed publish becomes two published videos.

## Shutdown

Stop accepting new jobs; signal cancellation; wait the grace period; abandon rather than force-kill any
worker holding an unresolved external intent, leaving it `UNKNOWN` for the next recovery pass; release
leases explicitly; checkpoint WAL; write the clean-shutdown marker.

An absent marker at next start escalates recovery: full intent sweep, artifact-store consistency check
and publication re-verification, rather than the fast path.

## Crash behaviour

The system assumes it can be killed at any instruction. The guarantees that make that survivable are:
committed intents before external calls (I-03), leases with expiry and fence tokens (I-05), single-statement
budget reservation (I-06), and atomic state-plus-event commits (I-02). Everything else is recoverable
because those four hold.
