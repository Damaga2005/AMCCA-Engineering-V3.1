# Fact Checking Policy

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## For content

1. Current facts come from research with a `retrieved_at`, never from a model (D-014).
2. A material claim needs `policy.research.min_sources` independent sources. Independence means distinct
   publishers, not distinct URLs.
3. A claim with any `CONTRADICTS` source is `DISPUTED` and can never be `VERIFIED`.
4. AI confidence is not evidence and never sets a claim status.
5. A source without `retrieved_at` cannot support any claim.
6. Recency windows are per subject class; a stale source cannot support a claim about current state.

## For platform capabilities specifically (V31-09)

The same "sourced, dated, never assumed" standard applies to what AMCCA believes a platform supports.
A capability discovered from a secondary source (a blog post, an agency roundup, community documentation)
is recorded as `DISCOVERED`, never `VERIFIED`. Only an official API response, an official dashboard, official
documentation, a direct platform probe, or an explicit operator confirmation can move a capability to
`VERIFIED` — enforced by a database `CHECK` on `platform_capabilities.evidence_source`, not left to review
discipline. See `SPEC/42`.

## For this specification package

The same standard applies to the package itself, and V2 failed it. Every assertion in this package about
an external system — a provider route, a platform requirement, a legal obligation, a price — MUST carry a
source reference and a retrieval date.

| Location | Requirement |
|---|---|
| `SPEC/23` | Gateway facts marked `UNVERIFIED` until retrieved and recorded |
| `SPEC/45` | Regulatory and platform facts carry URL and retrieval date; marked as requiring re-verification |
| `CONFIG/platforms.yaml` | `source_ref` and `retrieved_at` per platform rule set |
| `CONFIG/providers.yaml` | `evidence` block with URL and retrieval date; `capabilities_verified: false` until probed |
| `SPEC/34` | Media profiles carry `source_ref` and `retrieved_at` |

A rule set older than the configured staleness window degrades the associated capability to `UNVERIFIED`,
which blocks autonomous use (D-028). Staleness is handled by mechanism, not by remembering.

> *V2 defect closed:* `SPEC/20_OMNIROUTERS.md` asserted specific API routes and a correlation header with
> no URL and no timestamp, in direct violation of this policy as V2 itself stated it.
