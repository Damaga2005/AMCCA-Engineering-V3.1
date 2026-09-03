# 27 — Research Contracts

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Source contract

`IResearchSource` exposes `Search(query, constraints)` and `Fetch(url)`. Every implementation declares its
robots policy, rate limits, maximum response size, maximum fetch time and trust tier.

## Trust tiers

| Tier | Definition | Weight |
|---|---|---|
| `PRIMARY` | Originator of the fact: official statement, filing, dataset, peer-reviewed paper | Full |
| `SECONDARY` | Reporting that names its own source | Full, but two secondaries from the same primary count as one |
| `AGGREGATOR` | Republication without independent reporting | Corroborating only; never sole support for a `MATERIAL` claim |
| `UNRATED` | Unclassified | Never supports a `MATERIAL` claim |

The tier is a property of the source implementation and the retrieved document, assigned deterministically
by rules, not by an agent's impression of credibility.

## Retrieval record

Every fetch produces a `sources` row before any content is passed to a model. If the retrieval is not
recorded, the content does not exist as far as the claim system is concerned.

## Content handling

Retrieved content is stored by hash. Excerpts referenced by a claim are stored as hashes, not as full
copies, to limit the volume of third-party content retained. Full copies are kept only for the retention
window needed to substantiate a claim and are collected thereafter.

## Contract test

Every source implementation must pass a shared contract suite covering: robots refusal, oversize response,
slow response, redirect chain, malformed content, private-IP target, and non-UTF-8 content. A source that
has not passed this suite is not registered.
