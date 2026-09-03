# 77 — Installation

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Requirements

Windows 10 or 11 x64. FFmpeg available on `PATH` or at a configured location. Sufficient free space for
`storage.minimum_free_gb` plus working room.

## Install

Per-user by default. Creates the data directory, writes a default configuration, initialises the database
at the current migration version, and creates no credentials. First run shows the safety summary: no
publishing, `MANUAL` autonomy, `dry_run` on (D-020).

## Upgrade

1. Detect the installed version and its schema version.
2. Take a `PRE_MIGRATION` backup and verify it.
3. Replace binaries.
4. Apply migrations forward with checksum verification.
5. Run preflight.
6. On any failure, restore the backup and refuse to start, reporting the migration version reached.

Downgrade is not supported. A downgrade is a restore from a backup taken before the upgrade, which is why
step 2 exists and why its verification is not optional.

## Uninstall

Removes binaries. **Preserves the data directory by default.** Removing data is a separate, explicitly
confirmed action that states exactly what will be deleted, including how many productions and artifacts.

## Portable mode

A portable variant may run from a directory with a co-located data directory. In portable mode the secret
store falls back to DPAPI with a user-scoped key; the security note that this is weaker than a
machine-managed store is shown, not hidden.
