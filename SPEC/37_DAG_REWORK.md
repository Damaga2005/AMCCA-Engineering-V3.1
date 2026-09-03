# 37 — Rework and DAG Invalidation

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Algorithm

Given a QA finding referencing `responsible_artifact_version_id` **X**:

1. Locate X in the artifact DAG.
2. Compute all descendants through `artifact_edges`.
3. Mark descendants `SUPERSEDED` or `INVALIDATED`. **Never delete** (I-08).
4. Select the earliest repairable ancestor — the node whose regeneration can plausibly fix the finding,
   determined by the finding's `remediation_code`, not by an agent's opinion.
5. Verify `rework_attempts < policy.rework.max_attempts`; otherwise transition `T-2A1` to `FAILED`.
6. Reserve rework budget from the `REWORK` window; refusal transitions `T-2A2` to `BLOCKED`.
7. Enter `REWORK`, then transition to the producing state owning the selected node.
8. Regenerate only that node and its invalidated descendants.
9. Re-run QA from the earliest affected checkpoint, not from the beginning.
10. Detect repeated identical failure signatures and stop rather than loop.

## Scope discipline

Research is not regenerated for a render-only defect unless a DAG edge proves it is affected. This is the
whole value of maintaining lineage: without it, every failure costs a full pipeline run, and at that price
an autonomous system either stops reworking or stops being affordable.

## Failure signature

A hash over `(check_id, responsible node kind, expected, actual)`. Two consecutive identical signatures
mean regeneration is not converging; the loop stops and the production moves to `FAILED` with the evidence
attached. Retrying a deterministic failure is not resilience.

## Budget

Rework draws from the `REWORK` window, which is separate from and not fungible with the `PRODUCTION`
window (`SPEC/20`). A production cannot consume its entire budget in rework and leave nothing for the
work being reworked.

## Guarantees

- Rework is bounded in attempts and in cost.
- Rework is targeted, not global.
- No artifact is destroyed by rework; history remains reconstructable.
- Every rework cycle is visible: a transition, an event and a notification.
