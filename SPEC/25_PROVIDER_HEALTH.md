# 25 — Provider Health

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Measurement

Per provider and rate class, in rolling windows: success count, failure count, timeout count, latency
percentiles, rate-limit hits, and unresolved unknown-state count.

## Circuit breaker

`CLOSED -> OPEN` on a configured failure ratio within a window. `OPEN -> HALF_OPEN` after a cooldown.
`HALF_OPEN -> CLOSED` after a configured number of consecutive successes, or back to `OPEN` on any failure.

An open circuit blocks new dispatch to that provider. It never cancels in-flight work and never resolves
an outstanding unknown intent — reconciliation is exempt from the breaker, because refusing to reconcile
while a provider is degraded leaves ambiguity unresolved for exactly as long as it matters most.

## Rate limiting

Client-side limits per rate class, derived from documented provider limits where known and from observed
429 responses where not. A 429 tightens the local limit; sustained success gradually relaxes it.

## Health and scheduling

Provider health feeds scheduler backpressure. Sustained degradation stops new production cycles while
allowing publication, verification and reconciliation to continue.

## Unknown-state accounting

The unresolved unknown count is tracked separately from failures and is **not** counted toward the
circuit breaker ratio. An unknown is not a failure; conflating them would either open the circuit on
successful-but-unconfirmed operations or hide a genuine outage.
