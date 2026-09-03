# 21 — Pricing and Usage Reconciliation

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Pricing snapshots

Immutable rows carrying provider, model, unit, unit price, currency, `effective_at`, `retrieved_at` and
`source_ref`. A cost cannot be computed against a price lacking `retrieved_at` and `source_ref`.

Provider pricing is external, volatile and outside our control. Treating it as a constant in code is how a
system silently spends multiples of its intended budget after a price change.

## Estimation

Before execution, estimated cost is computed from the current snapshot and the predicted unit count, and
written as a `cost_events` row of kind `ESTIMATE`. The estimate drives the reservation, deliberately
rounded up rather than down.

## Actual usage

After execution, usage is read from the provider response and its request identifier. The raw provider
usage is stored in `units_json` **unmodified**, and the normalised cost is computed separately. Keeping
both means a provider changing its usage accounting is detectable rather than silently absorbed.

## Reconciliation states

| State | Meaning |
|---|---|
| `ESTIMATED` | Not yet executed or not yet settled |
| `RECONCILED` | Provider usage retrieved and matched to a request id |
| `ESTIMATED_UNRECONCILED` | Executed, but usage could not be retrieved |
| `DISPUTED` | Provider usage disagrees with the recorded operation |

An `ESTIMATED_UNRECONCILED` cost keeps its budget conservatively reserved until resolved. It is a known
unknown carried on the books, not a zero. A reconciliation backlog above a configured threshold triggers
scheduler backpressure (`SPEC/17`).

## Currency

Single configured currency for budgets and reporting. Provider prices in another currency are converted at
a recorded rate with its own retrieval timestamp; the original amount and currency are retained.
