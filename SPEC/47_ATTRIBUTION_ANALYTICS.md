# 47 — Attribution and Analytics

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Chain

`production -> publication -> attribution_event -> revenue_event`

Every link is explicit and recorded. An attribution that cannot name its publication does not enter the
chain; an unattributable revenue event is recorded as unattributed rather than assigned to the most likely
candidate. Plausible attribution is not attribution.

## Provenance

Every observation carries `API_MEASURED`, `IMPORTED`, `ESTIMATED` or `UNAVAILABLE`.
`analytics_snapshots` has `UNIQUE(publication_id, metric, window_start, provenance)`, so a measured value
and an estimate for the same window coexist as separate rows and the read path always prefers
`API_MEASURED` (I-12).

This is why the unique key includes provenance. Without it, whichever ingestion ran last would win, and a
late-arriving estimate would silently overwrite a measurement.

## Ingestion

Analytics jobs are P4. They are windowed, idempotent by `(publication_id, metric, window)` and safe to
re-run. A partial window is marked as such rather than presented as complete.

## Gaps

`UNAVAILABLE` is a first-class value. A metric a platform does not expose is recorded as unavailable, not
as zero. Zero and unknown are different facts and conflating them corrupts every aggregate built on top.

## Presentation

Every operator-facing number carries its provenance and its window. Measured and estimated values are
visually distinct (`SPEC/60`, rule 3). An aggregate mixing provenances states its composition.

## Learning input

Only `API_MEASURED` observations may update `hooks.measured_retention`, promote a niche to `PROVEN`, or
raise the confidence of a `memory_records` entry. Learning from estimates is learning from our own
guesses, which converges confidently on nothing.
