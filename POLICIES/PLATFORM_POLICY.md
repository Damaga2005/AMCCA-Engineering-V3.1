# Platform Policy

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Compliance posture

AMCCA operates within each platform's published terms and API terms. Where a capability is not offered by
an API, it is not accomplished by other means.

## Prohibited techniques

- Scraping a web interface to perform an action the API does not offer.
- Automating an interface that the platform's terms reserve for human interaction.
- Circumventing a rate limit rather than respecting it.
- Operating multiple accounts to evade a limit.
- Simulating an unsupported capability so a flow appears complete.

Each of these would work in the short term and each would put the operator's accounts at risk in the
medium term, which is not a trade an autonomous system should be making unattended.

## Rate limits

Client-side limits per rate class, derived from documented limits where known and observed 429s where
not. A 429 tightens the local limit immediately; sustained success relaxes it gradually.

## Capability gating

Nothing is dispatched without a `VERIFIED`, unexpired capability row for the specific account
(`SPEC/42`). Capability is per account: two accounts on the same platform can have materially different
permissions, and assuming otherwise produces failures that look random.

## Policy change response

An `AMCCA-PLT-002` authentication error or a rejection citing policy triggers immediate revalidation of
that account's capabilities and blocks autonomous publication to it until revalidation succeeds.

## Verification obligation

Platform rules encoded in `CONFIG/platforms.yaml` carry `source_ref` and `retrieved_at`. They are inputs
to be re-verified, not settled knowledge, and a stale rule set degrades the capability
(`POLICIES/FACT_CHECKING_POLICY.md`).
