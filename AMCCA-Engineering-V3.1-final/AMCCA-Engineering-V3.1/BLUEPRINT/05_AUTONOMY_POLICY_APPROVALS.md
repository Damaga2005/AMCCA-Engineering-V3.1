# Autonomy, Policy and Approvals

## Autonomy matrix

This table is normative. `SPEC/53` implements it; where they differ, this table wins (D-021).

| Capability | MANUAL | ASSISTED | AUTONOMOUS |
|---|---|---|---|
| Discover signals | operator triggers | automatic | automatic |
| Score opportunities | operator triggers | automatic | automatic |
| Research | operator triggers | automatic | automatic |
| Generate script | operator triggers | approval | automatic |
| Generate media | operator triggers | approval | automatic within budget |
| Use YELLOW rights asset | approval | approval | blocked |
| Use RED rights asset | blocked | blocked | blocked |
| Add affiliate program | approval | approval | blocked |
| Publish verified content | operator triggers | approval | only if `publishing_enabled` and every gate passed |
| Apply synthetic-content label | automatic | automatic | automatic |
| Skip synthetic-content label | blocked | blocked | blocked |
| Change security or content policy | approval | approval | blocked |
| Enable a model or capability | approval | approval | blocked |
| Increase own budget | blocked | blocked | blocked |
| Disable QA | blocked | blocked | blocked |
| Clear EMERGENCY_STOP | operator only | operator only | operator only |
| Bypass kill switch | blocked | blocked | blocked |

Four rows are `blocked` in every column. That is deliberate: they are the actions where an autonomous
system's judgement is worth least and the cost of being wrong is worst.

## Policy evaluation order

`Emergency stop -> Security -> Safety -> Rights -> Compliance/disclosure -> Platform -> Budget ->
Autonomy -> Operator configuration -> Strategy`

The order is fail-closed and short-circuits on the first `BLOCK`. Compliance sits above platform and
budget because a lawful-publication failure is not something a budget allowance can compensate for.

Every evaluation writes a `policy_decisions` row with the rule key, policy version and an input hash.
A protected action with no decision row is a test failure, not an oversight.

## Approvals

- Explicitly scoped to an action and a subject.
- Single-use by default; a reusable approval requires an explicit policy statement.
- Time-bounded: `expires_at` is mandatory.
- Identity and timestamp recorded.
- Auditable, and consumed exactly once.
- Cannot silently authorise a related-but-different action.

The data model has no representation for "approve everything". This is not an omission.

## Kill switch

`PAUSE_ALL`, `RESUME_ALL`, `STOP_CURRENT`, `CANCEL_QUEUE`, `DISABLE_PUBLISHING`, `EMERGENCY_STOP`.

State lives in `kill_switch_state`, a single-row table, so it survives restart as a matter of storage
rather than of care. `EMERGENCY_STOP` remains active until an operator clears it; no scheduler, agent or
recovery path may clear it.
