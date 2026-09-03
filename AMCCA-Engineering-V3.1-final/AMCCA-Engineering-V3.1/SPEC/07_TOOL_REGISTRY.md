# 07 — Tool Registry

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Side-effect classes

Every tool declares exactly one class. The class determines what the runtime requires before the call.

| Class | Meaning | Runtime requirement |
|---|---|---|
| `PURE` | Computation only | None |
| `READ` | Reads local state | Permission check |
| `LOCAL_WRITE` | Writes local files or rows | Permission check, path confinement, transaction |
| `EXTERNAL_IDEMPOTENT` | External call, safe to repeat | Idempotency key, bounded retry |
| `EXTERNAL_UNSAFE` | External call with a side effect that must not repeat | **Committed intent before the call**, idempotency key, no blind retry |

`EXTERNAL_UNSAFE` is enforced structurally: `tool_runs` carries
`CHECK(side_effect_class <> 'EXTERNAL_UNSAFE' OR intent_id IS NOT NULL)`, and `tool-run.schema.json`
carries the equivalent conditional. It is not possible to record a compliant unsafe call without an intent.

## Permissions

A tool declares required permissions; an agent contract declares granted tools. The intersection is
computed at invocation, not at registration, so a policy change takes effect immediately rather than at
next restart.

An agent calling an ungranted tool is blocked and audited. It is not "reminded" and re-prompted.

## Registry entry

`tool_id`, `tool_version`, purpose, input schema ref, output schema ref, `side_effect_class`,
required permissions, timeout, retry policy, rate class, cost model, failure codes.

## Rate and concurrency classes

Tools sharing an external dependency share a rate class. The worker supervisor enforces per-class
concurrency so that eight parallel productions cannot collectively exceed a provider limit that each
of them individually respects.

## Versioning

A change to a tool's input schema, output schema or side-effect class is a major version. Agents pin
tool major versions; a major bump requires each agent contract to be re-approved, because a tool whose
side-effect class changed is a different tool wearing the same name.
