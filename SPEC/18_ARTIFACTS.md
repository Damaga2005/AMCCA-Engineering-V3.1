# 18 — Artifacts and Lineage

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Model

`artifacts` is the logical item; `artifact_versions` holds immutable versions; `artifact_edges` holds the
lineage DAG; `artifact_manifests` seals a coherent set for a production.

## Identity

An artifact version is identified by its content hash. Two identical renders produce the same hash, which
makes duplicate detection free and makes tampering visible.

## Immutability

A version row is immutable except for its `state` field (`CURRENT`, `SUPERSEDED`, `INVALIDATED`,
`TOMBSTONED`). Content changes create a new version. Once a manifest is sealed, its versions cannot be
altered at all (I-07).

## Lineage

Every version records the versions it derives from. The graph is acyclic; a cycle-creating insert is
rejected before commit, because a cyclic lineage makes invalidation non-terminating.

Uses of the DAG:
- Targeted rework: find descendants of a defective node (`SPEC/37`).
- Retention: a version with live descendants is not collected.
- Reproducibility: reconstruct exactly which inputs produced an output.
- Export: package a production and everything it depends on, and no more.

## Manifests

A manifest lists every artifact version, its hash, size, kind, state and dependencies. Sealing happens at
`FINAL_VERIFIED`. Export verifies every hash against the manifest before packaging; import verifies before
accepting. A hash mismatch is a hard failure, never a warning.

## Deletion

Deletion writes a tombstone: the version row survives with `state = TOMBSTONED` and its metadata intact
(I-08). History remains reconstructable even when bytes are gone, which is what makes a retention policy
compatible with an audit trail.
