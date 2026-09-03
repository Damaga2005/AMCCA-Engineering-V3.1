# 56 — Backup and Recovery

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Backup types

| Type | Trigger |
|---|---|
| `PRE_MIGRATION` | Before every migration, without exception |
| `SCHEDULED` | On the configured cadence |
| `MANUAL` | Operator request |
| `PRE_RESTORE` | Before restoring, so a bad restore is itself recoverable |

## Verification

A backup is not considered a backup until verification succeeds: the copy is opened read-only, an
integrity check runs, and the schema version is recorded. Only then is `backups.verified` set to 1.
An unverified backup does not satisfy the release gate in `SPEC/79`.

## Scope

The database backup covers metadata. Artifacts are separately protected by the retention policy and the
hash manifest. A full disaster recovery therefore restores the database and re-verifies the artifact store
against the manifests, tombstoning versions whose files are gone rather than failing the restore.

## Restore

1. Take a `PRE_RESTORE` backup of the current state.
2. Stop the scheduler; drain workers.
3. Restore the database file.
4. Verify integrity and schema version.
5. Run the full recovery pass (`SPEC/16`), including reconciliation of every non-terminal intent.
6. Re-verify artifact presence against manifests.
7. Start.

Step 5 is not optional. A restored database contains intents whose real-world outcome happened after the
backup was taken, and the only safe assumption is that every one of them is ambiguous.

## Migration failure

A failed migration automatically restores the `PRE_MIGRATION` backup and refuses to start, rather than
running on a half-migrated database. It reports the migration version and the failure, and it does not
retry automatically.
