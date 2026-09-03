# 12 — Production State Machine

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

`SPEC/13` is the generated matrix and is authoritative for which transitions exist.
This file defines the semantics.

## Design rules

1. The Orchestrator is the **only** component that commits a production state change (I-01).
2. Every commit writes `productions.state`, a `state_transitions` row naming a transition id from
   `SPEC/13`, and an `events` row, in one transaction (I-02).
3. A transition absent from `SPEC/13` fails closed with `AMCCA-STM-001`. There is no permissive default.
4. Every state has at least one inbound transition and every non-terminal state at least one outbound
   transition. This is machine-verified (I-20).

Rule 4 is stated because V2 violated it: `REWORK`, `ARCHIVED` and `FAILED` were declared but unreachable,
and `UNKNOWN_EXTERNAL_STATE` — the safety mechanism the whole architecture rests on — had no way out.

## State families

| Family | States | Meaning |
|---|---|---|
| Producing | `RESEARCHING`, `SCRIPTING`, `STORYBOARDING`, `ASSET_GENERATION`, `AUDIO_GENERATION`, `EDITING` | Work is being generated; these are the states rework re-enters |
| Verified | `RESEARCH_VERIFIED`, `SCRIPT_VERIFIED`, `STORYBOARD_VERIFIED`, `ASSETS_READY`, `AUDIO_READY`, `CANDIDATE_RENDERED`, `FINAL_VERIFIED` | A deterministic check has passed |
| Gate | `CONCEPT_SELECTED`, `SCORING`, `READY_TO_PUBLISH` | A decision point |
| QA | six stages | Evaluation |
| Publish | `PUBLISHING`, `PUBLICATION_PROCESSING`, `PUBLICATION_VERIFIED` | Rollups over `publications` rows |
| Control | `REWORK`, `BLOCKED`, `UNKNOWN_EXTERNAL_STATE` | Non-linear paths |
| Terminal | `ARCHIVED`, `FAILED`, `CANCELLED` | No outbound transitions |

The producing/verified pairing is deliberate and was added in V3. V2 had `ASSETS_READY` with no state
representing "assets are being generated", which left rework with nowhere legal to return to.

## Rework

A QA finding names `responsible_artifact_version_id`. `DagService` resolves the earliest repairable
ancestor. The production enters `REWORK`, then re-enters the producing state that owns that ancestor.
Descendants are marked `SUPERSEDED` or `INVALIDATED`, never deleted. Bounded by
`policy.rework.max_attempts` and by repeated-failure-signature detection.

## Blocking and resuming

Entering `BLOCKED` persists `blocked_from`. Resuming is legal only to that state, after the blocking
condition is cleared and an authorised approval is recorded. A resume to any other state is
`AMCCA-STM-002`.

## Unknown external state

Entering persists `unknown_from`. Only the reconciliation service exits it, and only with evidence:
the operation did not happen (return to `unknown_from`), it was accepted, it completed, it definitively
failed, or it cannot be resolved within bounds (`BLOCKED`, operator notified).

## Publication rollup

Production-level publish states are computed from `publications` rows by rules R-1 to R-5 in `SPEC/13`.
A partial outcome — some targets verified, some failed — holds the production in
`PUBLICATION_PROCESSING` and notifies the operator. It is never rounded up to success.
