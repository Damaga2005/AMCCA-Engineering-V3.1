# 53 — Kill Switch and Autonomy

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Modes

| Mode | Effect |
|---|---|
| `NORMAL` | Everything permitted by policy runs |
| `PAUSED` | No new work dispatched; in-flight work completes; P0 control work still runs |
| `PUBLISHING_DISABLED` | Everything runs except publication dispatch |
| `EMERGENCY_STOP` | All work halted; in-flight external operations are abandoned to `UNKNOWN` for later reconciliation rather than force-completed |

## Persistence

State lives in `kill_switch_state`, a single-row table read during startup step 6. `EMERGENCY_STOP`
therefore survives restart as a property of storage rather than of anyone remembering (I-16). It can be
cleared only by an operator, and clearing is audited.

## Why in-flight work is abandoned, not completed

An emergency stop is invoked because something is wrong. Allowing in-flight external operations to
complete during one assumes the thing that is wrong is not those operations. Abandoning them to `UNKNOWN`
is the conservative choice: it costs a reconciliation pass and it cannot make the incident worse.

## Autonomy modes

`MANUAL`, `ASSISTED`, `AUTONOMOUS`, defined by the matrix in `BLUEPRINT/05`, which is normative.
This file implements it; where they differ, the Blueprint wins (D-021).

## Elevation

Only an operator can raise autonomy. The change is audited with a timestamp and identity. No agent,
scheduler or recovery path can raise it, and there is no configuration file setting that takes effect
without a restart and a fresh preflight (`SPEC/03`).

Raising to `AUTONOMOUS` additionally requires: `providers.gateway.capabilities_verified = true`, a second
`IProviderGateway` implementation present (D-013), and every suite in `SPEC/72`-`SPEC/74` passing.

## Degradation triggers

The system reduces its own effective autonomy — never raises it — on: budget threshold `PAUSE`,
sustained provider degradation, reconciliation backlog above threshold, dead-letter count above threshold,
disk below minimum, or any `SECURITY`-class error. Degradation is announced, not silent.
