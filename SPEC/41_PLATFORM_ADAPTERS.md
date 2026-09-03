# 41 — Platform Adapters

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Contract

`IPlatformAdapter` exposes: `VerifyCapabilities`, `PrepareUpload`, `Upload`, `GetStatus`, `GetMetrics`,
`ApplySyntheticLabel`, `ListRecent`.

`ListRecent` exists for one reason: it is the fallback that lets reconciliation determine whether an
ambiguous upload actually landed. An adapter without it cannot be used for autonomous publication,
because its ambiguities are unresolvable.

## Required behaviours

| Behaviour | Requirement |
|---|---|
| Capability verification | Live probe; result written with evidence and `verified_at`. A secondary source (documentation aggregator, blog) can only produce `DISCOVERED`, never `VERIFIED` — see `SPEC/42` (V31-09) |
| Upload | Intent committed first; idempotency key sent where supported |
| Status | Authoritative endpoint only; never inferred from the upload response |
| Synthetic label | Applied through the platform's own mechanism where one exists (`SPEC/45`) |
| Metrics | Returned with provenance; never estimated by the adapter |
| Errors | Mapped to the `SPEC/05` catalogue, never surfaced raw |
| Ambiguity | Returned as unknown; the adapter never guesses |

## What an adapter must not do

- Scrape a web interface to accomplish what the API does not offer.
- Simulate an unsupported capability.
- Report success on a 200 that only acknowledges receipt.
- Retry an unsafe operation on its own initiative.
- Log a token, a full request body or a full response body.

## Contract test suite

Every adapter passes a shared suite covering: success, 401, 403, 404, 429 with and without `Retry-After`,
500, timeout after send, timeout before send, malformed body, partial upload, duplicate idempotency key,
and status-endpoint disagreement with the upload response. An adapter that has not passed this suite is
not registered.

The "timeout after send" case is the one that matters. An adapter that cannot distinguish it from
"timeout before send" must report unknown for both, and that is the correct behaviour.
