# 65 — Optional Localhost API Boundary

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Status: optional, disabled by default

D-011 states that a localhost HTTP API is **not required** for core operation. This file specifies what it
must do **if** it is enabled, so that enabling it does not become an unspecified attack surface.

> *V2 defect closed:* the V2 package referenced an OpenAPI document by a relative path that did not
> resolve, and specified two different endpoint counts in two different files.

## If enabled

| Requirement | Detail |
|---|---|
| Binding | Loopback only. Binding to a non-loopback address is refused at startup |
| Authentication | A locally generated bearer token stored in the secret store; no anonymous access |
| Scope | Read-only queries plus a strictly enumerated command subset, never the full command catalogue |
| Protected actions | Still routed through the policy engine; the API is a caller, not an authority |
| Contract | `SCHEMAS/openapi.yaml`, generated from the command and query contracts |
| Rate limiting | Per-token, configured |
| Logging | Every request audited with its correlation id |
| CORS | Disabled |

## What it must never expose

Secrets or `secret://` resolution, raw provider payloads, personal-data-flagged content, the ability to
change autonomy or policy, the ability to clear `EMERGENCY_STOP`, or the ability to publish without the
same preflight the UI path uses.

## Generation and drift

`SCHEMAS/openapi.yaml` is generated from the command and query contracts and is checked by
`TOOLS/validate_package.py`. A hand-edited OpenAPI document that disagrees with the commands it claims to
describe is worse than none, because it is trusted.
