# 29 — Trends, Niches and Opportunities

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Trends

A trend is an observation with a source. `trends.source_id` is `NOT NULL` and references `sources`,
so a trend that cannot name where it came from cannot be stored (D-014). Trends carry `observed_at` and
`expires_at`; an expired trend does not contribute to scoring.

## Niches

`CANDIDATE -> TESTING -> PROVEN -> RETIRED`.

`PROVEN` is reachable only from measured analytics meeting a configured evidence bar: a minimum number of
publications, a minimum measurement window, and measured performance above threshold. A forecast cannot
promote a niche. This is the difference between learning and confirmation bias.

## Opportunity scoring

Deterministic. The agent may supply qualitative reads; the score is computed by code from:

`score = f(trend strength, niche fit, evidence availability, expected revenue, expected cost, risk penalty)`

with the weights recorded in `score_breakdown_json` so any score can be explained after the fact.
Two runs with the same inputs produce the same score, and a test asserts it.

## Estimates stay estimates

`expected_revenue` and `expected_cost` are stored on `opportunities` and never written to
`revenue_events` or treated as settled cost (D-030, I-13). The database `CHECK` on
`revenue_events.provenance` makes the mistake unrepresentable rather than merely discouraged.

## Selection

Selection is a policy-gated decision recorded as the `CONCEPT_SELECTED` transition with its rationale and
an expected-value snapshot. In `MANUAL` and `ASSISTED` modes the operator selects; in `AUTONOMOUS` mode
the scheduler selects the highest-scoring eligible opportunity within budget.
