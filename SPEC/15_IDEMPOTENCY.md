# 15 — Idempotency

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Key construction

A key is derived deterministically from `operation_type + stable_entity_id + intent_version`.
It is a pure function of those inputs. It is never random, never time-based, and never regenerated for a
retry of the same logical intent.

A random key on retry is not idempotency; it is a guarantee of duplication under exactly the conditions
idempotency exists to handle.

## Enforcement

`jobs.idempotency_key`, `intents.idempotency_key` and `publications.idempotency_key` all carry `UNIQUE`
constraints. Duplication is prevented by the database, not by a check-then-act in application code, which
is unsound under concurrency.

## Intent persistence

Before any `EXTERNAL_UNSAFE` call:

1. Insert `intents` with state `CREATED`, the idempotency key and a request fingerprint.
2. **Commit.**
3. Make the call.
4. Record the outcome.

The fingerprint is a hash of the exact request body and target. It allows reconciliation to recognise our
own request in a provider's records, and it detects a caller that changed the payload while reusing a key.

## Unknown result

On timeout or connection loss after dispatch, the intent becomes `UNKNOWN`. The system does not retry and
does not create a second intent. Reconciliation resolves it first. This is a single rule and it is not
subject to a "but the operation is probably cheap" exception, because probability is not evidence.

## Provider-side idempotency

Where a provider supports an idempotency header, the same key is sent. Where it does not, reconciliation
by request fingerprint and external listing is the fallback. Where neither is possible, the capability is
marked as requiring manual confirmation and is excluded from autonomous operation (D-028).
