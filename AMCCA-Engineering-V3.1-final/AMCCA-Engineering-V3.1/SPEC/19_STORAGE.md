# 19 — Storage

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Layout

```
{data_root}/
  amcca.db                database, WAL and shm
  artifacts/{aa}/{hash}   hash-addressed content, two-level fan-out
  temp/{job_id}/           scratch, deleted on completion or recovery
  cache/                   re-derivable data, safe to delete at any time
  logs/                    rotated structured logs
  backups/                 verified database backups
  exports/                 generated export packages
```

The fan-out exists because a flat directory with hundreds of thousands of entries degrades badly on
NTFS, and this system is designed to accumulate exactly that.

## Space management

`storage.minimum_free_gb` is checked at preflight and before every render dispatch. Breaching it blocks
new work rather than failing mid-operation. Estimated output size is part of the dispatch decision.

## Retention clocks

Separate per class; see `SPEC/52`. Collection respects live DAG references, pending reconciliation, sealed
manifests and legal or rights holds. A held artifact is never collected regardless of age.

## Integrity

Every write is hash-verified after completion. A periodic sweep detects files without rows (orphans, to be
collected) and rows without files (corruption, to be tombstoned with `AMCCA-STO-002`).

## Path safety

All paths are canonicalised and confined beneath `data_root`. Traversal sequences are rejected. Extensions
are allow-listed by artifact kind. Archives are validated for entry count, total uncompressed size and
per-entry path before extraction (`AMCCA-SEC-004`). Nothing outside `data_root` is ever written.
