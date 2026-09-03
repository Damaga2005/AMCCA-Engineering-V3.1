# 31 — Hook Engine

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Purpose

Generate and select opening hooks, and measure which ones actually retained attention.

## Generation

The hook agent proposes candidates. Deterministic screening rejects candidates that: exceed the length
limit for the target platform, assert a non-`VERIFIED` claim, breach content policy, promise something the
content does not deliver, or duplicate a recently used hook above the similarity threshold.

The "promises something the content does not deliver" check is deterministic and structural: every factual
assertion in the hook must map to a claim that the script also asserts.

## Selection

Ranked by measured retention of similar patterns from `memory_records`, subject to the confidence floor.
Below the floor, the ranking is presented to the operator as a suggestion rather than applied
automatically (`SPEC/22`).

## Measurement

After publication, retention metrics are attributed back to `hooks.measured_retention` with provenance.
Only `API_MEASURED` observations update it. This closes the loop that makes the pattern library worth
having, and keeps it from being trained on guesses.

## Anti-pattern guard

A pattern whose measured retention degrades across a configured number of uses is retired automatically.
Content ecosystems saturate; a hook that worked ten times is a hook the audience has now seen ten times.
