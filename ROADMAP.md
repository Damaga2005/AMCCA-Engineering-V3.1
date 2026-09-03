# Roadmap

The roadmap is a delivery narrative. `BUILD_ORDER.md` is the binding sequence; where they differ,
`BUILD_ORDER.md` wins.

## Phase 0 — Foundation
The application compiles, launches, validates configuration, migrates the database, stores a secret,
renders a test media file, and runs its own package validator in CI.

## Phase 1 — Durable execution
Jobs, leases with fence tokens, the production state machine, append-only events, idempotency keys,
crash recovery and intent reconciliation. Nothing above this line is trustworthy until this line holds.

## Phase 2 — Intelligence and evidence
Provider gateway port with a working adapter, model registry with capability probing, research sources
with retrieval timestamps, claims with source linkage, deterministic opportunity scoring.

## Phase 3 — Production
Script through render, artifact lineage as a DAG, deterministic QA, AI-assisted QA as evidence only,
rights records, duplicate detection, bounded targeted rework.

## Phase 4 — Distribution
Platform adapters, OAuth with PKCE, capability matrix with expiry, publication intents, reconciliation,
synthetic-content disclosure, publication verification from authoritative evidence.

## Phase 5 — Money and learning
Referral validation, attribution chains, measured revenue, cost settlement and reconciliation, profit over
confirmed revenue only, memory, genome and experiments.

## Phase 6 — Autonomous operation
Policy-approved autonomous schedules, budget reservations under concurrency, kill switch, continuous
reconciliation. Entered only after every earlier phase is green and an operator explicitly enables it.

## What is deliberately not on this roadmap

A remote backend, a multi-user model, a mobile client and a plugin marketplace. Each would change the
security model materially and none is required by the product boundary in D-001.
