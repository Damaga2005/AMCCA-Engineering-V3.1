# 10 — Database Engine and Transaction Rules

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

`SPEC/11` defines every table. This file defines how the database is operated.

## Engine settings

| Setting | Value | Asserted |
|---|---|---|
| Journal mode | WAL | At startup; a non-WAL database aborts with `AMCCA-DB-001` |
| `foreign_keys` | ON | Per connection; off aborts |
| `synchronous` | NORMAL steady state, FULL around migrations, backups and exports | Per operation |
| `busy_timeout` | Configured, non-zero | Per connection |
| `temp_store` | MEMORY | Per connection |

Foreign keys are asserted per connection rather than assumed, because SQLite defaults them off and a
single connection that forgets is enough to accumulate orphans silently.

## Migrations

Forward-only, numbered, checksummed. Each records `version`, `name`, `checksum`, `applied_at`,
`applied_by`. A migration whose recorded checksum differs from the shipped file aborts startup with
`AMCCA-DB-002` — the database is not run against a file that has changed since it was applied.

A `PRE_MIGRATION` backup is taken and verified before any migration runs. A failed migration restores it
and refuses to start.

Rollback scripts exist for every migration and are exercised in the test suite. A migration that has never
been rolled back in a test is a migration whose rollback does not work.

## Transaction rules

1. The eight atomic units in `SPEC/11` are the complete list. New atomic units require a SPEC change.
2. **No transaction contains a network call, a media render or a user prompt** (I-22). A concurrency test
   asserts this by failing any transaction exceeding a configured wall-clock ceiling.
3. Transactions are short. Long transactions in WAL mode grow the WAL and block checkpointing.
4. Optimistic concurrency via `aggregate_version`; the unique index on
   `events(aggregate_type, aggregate_id, aggregate_version)` converts a lost update into a constraint
   violation instead of silent corruption.
5. Job claiming and budget reservation are single conditional `UPDATE` statements. Read-then-write for
   either is forbidden and covered by a concurrency test.

## Backups

Online backup before migrations and on the configured schedule. Verification opens the copy read-only and
runs an integrity check; `backups.verified` is set only after that succeeds. An unverified backup does not
satisfy the release gate, because an unverified backup is a file, not a backup.

## Integrity checks

On startup: `PRAGMA integrity_check` on a schedule; orphan sweep for artifact rows without files and
files without rows; verification that no production is in `BLOCKED` without `blocked_from` or in
`UNKNOWN_EXTERNAL_STATE` without `unknown_from`.
