# 66 — Performance

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Targets

These are operational targets for a single-user desktop system on typical hardware, not guarantees:

| Operation | Target |
|---|---|
| UI interaction response | Under 100 ms; never blocking on I/O |
| Dashboard load | Under 1 s with 100k events |
| Production inspector load | Under 2 s for a production with full lineage |
| Job claim | Under 10 ms |
| Budget reservation | Under 10 ms |
| Event append | Under 5 ms |
| Package validator | Under 30 s |

## Scale assumptions

Hundreds of productions, tens of thousands of artifact versions, hundreds of thousands of events, tens of
thousands of claims and sources. SQLite handles this comfortably; the failure mode is not the database
but unpaged UI lists and unindexed queries.

## Query discipline

Every query the UI issues must be index-supported. A query plan review is part of adding a screen.
`SPEC/11` lists the indexes; adding a query that needs a new one means adding it there, not adding it
ad hoc in code.

## Media and concurrency

FFmpeg concurrency is capped independently of the general worker pool because it is CPU- and IO-hungry
and an unbounded pool starves the UI thread and the scheduler. The cap is configuration, defaulted
conservatively.

## Growth management

WAL checkpointing on a schedule and at shutdown. Retention keeps event and log growth bounded. Artifact
storage growth is the operator's to manage, and the dashboard surfaces it before it becomes urgent rather
than when the disk is full.
