# External Integrations

## The port rule

Every external system is reached through a port. No code above the adapter layer names a vendor, a
route, a header or a model. This is D-013, and it is the difference between a supplier change costing
two weeks and costing a rewrite.

| Port | Adapters | Capability discovery |
|---|---|---|
| `IProviderGateway` | OmniRouters (primary), one alternate (required before autonomy) | Live probe writes `model_registry.last_verified_at` |
| `IPlatformAdapter` | One per platform | Live probe writes `platform_capabilities` with `expires_at` |
| `IResearchSource` | One per source family | Robots and rate policy per source |
| `IAffiliateProvider` | One per program family | Validation evidence recorded per `referral_links` |
| `ISecretStore` | DPAPI / Credential Manager | Availability asserted at preflight |

## Intent before effect

Every mutation of external state follows the same six steps, without exception:

1. Compute a deterministic idempotency key from operation type, stable entity id and intent version.
2. Insert an `intents` row in state `CREATED` and **commit the transaction**.
3. Make the call, outside any transaction.
4. On a definitive response: record `CONFIRMED` or `REFUTED` with the provider request identifier.
5. On no definitive response: record `UNKNOWN`. Do not retry. Do not create a second intent.
6. Reconciliation resolves `UNKNOWN` against an authoritative status source before anything else happens.

Step 5 is the whole point. Almost every duplicate-publication incident in systems of this kind is a
timeout that was optimistically retried.

## Capability verification

A capability is one of `VERIFIED`, `UNVERIFIED`, `DISABLED`, `UNSUPPORTED`. Only `VERIFIED` and unexpired
capabilities may execute autonomously. Verification is periodic and its result is stored with evidence
and a timestamp. An expired verification degrades to `UNVERIFIED`, which blocks autonomous use — the
system does not assume that what worked last month still works.

## What we do not do

- We do not scrape a platform to accomplish something its API does not support.
- We do not simulate an unsupported capability to make a flow look complete.
- We do not treat a 200 response as proof of anything beyond "the request was received".
- We do not hardcode model names, prices or platform limits as constants. They are runtime data with
  retrieval timestamps.

## Standing verification obligation

Facts about external systems in this package — gateway routes, platform capabilities, labelling duties,
pricing — are **inputs to be re-verified**, not settled knowledge. Each such statement in SPEC carries a
source reference and a retrieval date, per `POLICIES/FACT_CHECKING_POLICY.md`. A statement without one
is a specification defect and the validator flags it.
