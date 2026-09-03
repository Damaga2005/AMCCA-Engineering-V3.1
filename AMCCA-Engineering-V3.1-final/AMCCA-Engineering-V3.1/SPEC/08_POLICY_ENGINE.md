# 08 — Policy Engine

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Position

The policy engine is consulted **before** a protected action, never after. It returns
`ALLOW`, `REQUIRE_APPROVAL` or `BLOCK`, and writes a `policy_decisions` row every time. It performs no
side effects of its own; a component that both decides and acts cannot be audited.

## Protected actions

Any action that spends money, mutates external state, touches credentials, changes policy or autonomy,
uses a non-GREEN asset, publishes, or enables a capability.

## Evaluation order

`Emergency stop -> Security -> Safety -> Rights -> Compliance/disclosure -> Platform -> Budget ->
Autonomy -> Operator configuration -> Strategy`

Fail-closed, short-circuiting on the first `BLOCK`. Compliance is deliberately above budget: a labelling
or rights failure is not a cost to be weighed, it is a stop.

## Decision record

`production_id`, `action`, `decision`, `rule_key`, `policy_version_id`, `inputs_hash`, `correlation_id`,
`decided_at`. The `inputs_hash` makes a decision reproducible: given the same inputs and the same policy
version, the engine must return the same answer, and a test asserts it.

## Policy versioning

Policies are versioned and immutable once created. Activation is an audited operator action recorded in
`policy_versions.activated_by`. `audit_log.actor_type` has no `AGENT` value, so an agent cannot appear as
the activator of a policy even in a corrupted state.

## Determinism requirement

The engine is pure with respect to `(action, subject snapshot, policy version, clock)`. It does not call
a model, does not perform I/O, and does not consult a cache that can be stale. This is what makes
`inputs_hash` meaningful and what allows the whole decision history to be replayed after an incident.
