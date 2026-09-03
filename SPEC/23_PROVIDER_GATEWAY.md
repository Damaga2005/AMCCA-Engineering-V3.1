# 23 — Provider Gateway

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## The port

`IProviderGateway` exposes capability-typed operations: text, image, video, speech, embedding. Nothing
above the adapter layer references a gateway-specific type, route or header (D-013).

## Current adapter: OmniRouters

**Verification status: UNVERIFIED at package authoring time.**

The V2 package asserted specific facts about the OmniRouters API — base URL, Bearer auth,
OpenAI-compatible chat routes, image and video routes, async task lookup, and an `X-Oneapi-Request-Id`
correlation header — with no source reference and no retrieval timestamp, in direct violation of its own
`FACT_CHECKING_POLICY`. Those assertions are **not** carried forward as facts here.

Additionally, at least four distinct projects use closely similar names (`omnirouter`, `OmniRouter`,
`OmniRoute`, `omnirouters`), so even the identity of the intended service is ambiguous from the name alone.

**Implementation obligation.** Before writing the adapter:

1. Retrieve the current official documentation and record its URL and retrieval date in
   `CONFIG/providers.yaml` under `evidence`.
2. Confirm the service identity unambiguously (organisation, domain ownership, support channel).
3. Probe each capability live and record the result in `model_registry` with `last_verified_at`.
4. Leave `capabilities_verified: false` until steps 1-3 succeed. Autonomous mode is refused while false
   (`SPEC/03`, consistency rule 4).

Do not implement from the description in any prior AMCCA document. Implement from the documentation you
retrieved, and record what you retrieved.

## Adapter requirements, independent of vendor

| Requirement | Reason |
|---|---|
| Capture a provider request identifier where one is returned | Cost reconciliation (`SPEC/21`) |
| Never log the API key or full request bodies | D-007, I-14 |
| Persist an async task identifier before polling | A crash mid-poll must not lose the task |
| Normalise provider terminal states to AMCCA states | The domain must not learn a vendor vocabulary |
| Bounded polling with provider-specific intervals | Rate-limit safety |
| Fail closed on an unrecognised capability | D-028 |
| Treat an ambiguous request as `UNKNOWN_EXTERNAL_STATE` | D-016 |

## Second adapter requirement

**A second `IProviderGateway` implementation MUST exist before autonomous mode is enabled** (D-013).
A port with one implementation has never been tested as a port, and the whole multimodal capability of
this system depends on a single external service. The second implementation may be minimal — text
capability only is sufficient — but it must be real and exercised by the contract test suite.

## Model registry

Model names are runtime data, never constants. Each entry records capability, protocol, enabled flag,
constraints, a pricing snapshot reference, `last_verified_at` and fallback order.
`CHECK(enabled = 0 OR last_verified_at IS NOT NULL)`: a model cannot be enabled without a probe.
