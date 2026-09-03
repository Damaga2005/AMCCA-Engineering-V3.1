# Safety Policy

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Fail closed

Every gate fails closed. An unavailable check is a blocked action, not a permitted one. Absence of
evidence is treated as evidence of absence for the purpose of granting a permission (D-028).

## Bounded everything

| Bounded | Mechanism |
|---|---|
| Retries | `max_attempts` plus cumulative retry cost |
| Rework | `policy.rework.max_attempts` plus failure-signature detection |
| Reconciliation | `policy.reconcile.max_attempts`, then `BLOCKED` |
| Spend | Five budget windows, most restrictive binds |
| Concurrency | Global, per-provider and per-platform caps |
| Agent execution | Timeout and cost ceiling per contract |
| Media | Timeout and output size ceiling per invocation |

An unbounded loop in an autonomous system that spends money is the failure that does not stop on its own.

## Ambiguity handling

`UNKNOWN_EXTERNAL_STATE` is never converted to success or failure without reconciliation evidence
(D-016). There is no exception for cheap operations, small uploads or platforms that usually respond.

## Human authority

Only an operator can raise a permission, clear an emergency stop, approve a protected action, or confirm
an outcome manually. Manual confirmation is recorded as `OPERATOR_CONFIRMATION` evidence, which is
weaker than API evidence and is labelled as such wherever it appears.

## Degradation, not failure

When conditions worsen — provider degradation, reconciliation backlog, low disk, budget pause — the
system reduces what it originates while continuing to complete, verify and reconcile what is already in
flight. Producing more work while unable to resolve existing ambiguity is how a small incident becomes a
large one.
