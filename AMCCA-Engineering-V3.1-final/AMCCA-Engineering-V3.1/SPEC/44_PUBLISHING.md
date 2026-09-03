# 44 — Publishing Protocol

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Sequence

1. Create an immutable publication row referencing the exact content version, platform account, metadata
   version and referral version.
2. Run publication preflight (`SPEC/49`). Any failure blocks; nothing is dispatched.
3. Acquire the publication lock for `(production_id, platform, account_id)`.
4. Persist the upload intent with its idempotency key and **commit**.
5. Call the adapter.
6. Persist the provider request identifier, external identifier and returned status.
7. If accepted but not final, enter `PROCESSING`.
8. Poll or reconcile using the platform's authoritative status endpoint.
9. Only authoritative evidence transitions to `PUBLISHED` and then `VERIFIED`, recording
   `evidence_source` and `evidence_retrieved_at`.
10. Release the lock and emit events.

## The timeout rule

**A timeout after request submission always produces `UNKNOWN_EXTERNAL_STATE`, unless the adapter can
prove the request was never sent.** There is no exception for small uploads, cheap operations or
platforms that "usually" respond. This single rule is the difference between one published video and two.

## Duplicate prevention

Three independent layers:
1. The publication lock avoids concurrent dispatch.
2. The idempotency key lets the platform deduplicate where it supports that.
3. `UNIQUE(production_id, platform, account_id, content_version_id)` makes a duplicate row impossible
   even if layers 1 and 2 both fail (I-17).

Layer 3 is the one that actually guarantees the property, because it does not depend on our code being
correct at the moment it matters.

## Multi-target

Each target is its own `publications` row with its own state, evidence and idempotency key. The
production-level state is a rollup by rules R-1 to R-5 in `SPEC/13`. A partial outcome holds the
production in `PUBLICATION_PROCESSING` and notifies the operator; it is never rounded up to
`PUBLICATION_VERIFIED`.

## Retraction

An operator may retract a publication where the platform supports deletion. Retraction is itself an
`EXTERNAL_UNSAFE` operation with its own intent and its own reconciliation. The original publication row
and its evidence are retained; retraction is recorded, not erased.
