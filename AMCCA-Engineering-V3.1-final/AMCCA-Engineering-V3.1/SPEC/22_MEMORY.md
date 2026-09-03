# 22 — Memory and Learning Substrate

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## What memory is

`memory_records` stores durable, evidence-backed generalisations: which hooks measurably retained
attention, which niches converted, which media profiles passed QA first time, which providers failed.

## What memory is not

It is not a cache of model outputs, not a store of raw prompts, and not a place to keep personal data.
It is also not authority: a memory record is an input to a deterministic decision, never a decision.

## Confidence

Every record carries a confidence value derived from the volume and quality of the measured evidence
behind it. A record derived from a single production, or from an unmeasured outcome, carries confidence
below 0.5 and **cannot drive an autonomous decision**. It may inform an operator-visible suggestion.

This is the guard against the characteristic failure of self-learning content systems: converging
confidently on a pattern that was one lucky result.

## Provenance

Each record carries `evidence_ref` pointing at the measured observations that produced it. A record whose
evidence has been deleted under retention is downgraded, not silently retained at full confidence.

## Decay

Records age. Confidence decays on a configured half-life unless refreshed by new measured evidence,
because the environment this system operates in changes faster than the records describing it.

## Isolation

Memory never crosses niches or languages without an explicit generalisation rule. A hook that worked in
one context is not evidence for another, and treating it as such is how a system develops a house style
nobody asked for.
