# 54 — Observability

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Structured logging

Serilog with a redaction stage ahead of every sink. Every log entry carries `correlation_id` and, where
applicable, `production_id`, `job_id` and `causation_id`. Free-text-only log entries are not acceptable
for anything an operator might need to search.

## Metrics

| Group | Metrics |
|---|---|
| Jobs | Queue depth by priority, throughput, retry rate, dead-letter count, lease expiries |
| Providers | Latency percentiles, error rate, 429 rate, circuit state, unresolved unknown count |
| Productions | Cycle time by stage, QA pass rate first time, rework rate, rework depth |
| Money | Reserved, settled, unreconciled, confirmed revenue, budget utilisation per window |
| Publishing | Dispatch rate, verification lag, reconciliation backlog, duplicate-prevented count |
| Storage | Free space, artifact bytes, collection volume, orphan count |

`duplicate-prevented count` deserves attention in review: a non-zero value means the unique constraint
caught something the lock and the idempotency key did not, which is worth understanding.

## Tracing

Correlation identifiers propagate through job, agent run, tool run, intent, publication and event.
Given any one of them, the whole operation is reconstructable. This is the practical payoff of the
`correlation_id` / `causation_id` requirement in D-018, and the reason its absence from the V2 event
schema was a serious defect rather than a cosmetic one.

## Health surface

The UI shows: kill switch and autonomy state, scheduler state, worker utilisation, provider circuit
states, reconciliation backlog, dead-letter count, budget utilisation and disk headroom. These are the
numbers that predict trouble; they are on the dashboard, not behind a menu.

## What is never logged

Secrets, authorization headers, cookies, tokens, full provider request or response bodies, full retrieved
source documents, and personal-data-flagged content. Verified by test, not by convention (`SPEC/72`).
