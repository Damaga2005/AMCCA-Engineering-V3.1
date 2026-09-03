# 26 — Research Engine

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Purpose

Produce timestamped, source-backed evidence. The research engine is the only path by which current facts
enter the system (D-014). A model's output is never treated as current truth.

## Flow

1. Build a query plan from the opportunity and niche.
2. Fetch from configured sources under the security rules of `SPEC/28`.
3. Store each retrieval as a `sources` row with URL, publisher, `published_at` where available,
   `retrieved_at`, content hash and trust tier.
4. The research agent proposes claims linked to candidate sources.
5. Deterministic validation sets each claim's status. **The agent never sets status.**

## Claim validation

| Rule | Effect |
|---|---|
| A `MATERIAL` claim needs at least `policy.research.min_sources` independent `SUPPORTS` sources | Otherwise it cannot reach `VERIFIED` |
| Independence means distinct publishers, not distinct URLs | Three aggregators republishing one wire story is one source |
| A source with no `retrieved_at` cannot support any claim | Enforced by `NOT NULL` |
| A claim with any `CONTRADICTS` source becomes `DISPUTED`, never `VERIFIED` | Contradiction is surfaced, not averaged away |
| Claims about people, health, finance, law or breaking events require the stricter bar in `CONTENT_POLICY` | `subject_class` drives it |
| Stale sources beyond a configured recency window cannot support a claim about current state | Recency is per subject class |

## Statuses

`VERIFIED`, `DISPUTED`, `ESTIMATED`, `UNKNOWN`. AI confidence alone can never produce `VERIFIED`.
A script may only assert a `VERIFIED` claim as fact; `ESTIMATED` and `DISPUTED` claims must be worded with
their uncertainty, and `UNKNOWN` claims cannot appear at all.

## Personal data

A claim containing personal data sets `contains_personal_data = 1`, which triggers minimisation, a shorter
retention clock and export exclusion (`SPEC/51`).

## Failure

Insufficient evidence after `policy.research.max_attempts` moves the production to `BLOCKED` with
`AMCCA-RES-001` and an operator notification. It does not proceed with weaker evidence, and it does not
quietly reduce the source requirement.
