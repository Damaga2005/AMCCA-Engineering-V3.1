# 48 — Experiments and Content Genome

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Genome

A production's genome is the structured set of parameters that produced it: hook pattern, pacing profile,
voice profile, visual style, duration band, disclosure placement, media profile, prompt versions and model
identifiers. It is derived from recorded artifact metadata, not declared separately, so it cannot drift
from what was actually produced.

## Experiments

An experiment declares a hypothesis, a target metric, variants and a minimum sample. Variants differ in
one genome dimension at a time; a multi-dimensional variant produces a result nobody can attribute.

`DRAFT -> RUNNING -> CONCLUDED` or `ABANDONED`. An experiment cannot be `CONCLUDED` with fewer than
`min_sample` **measured** observations. The constraint is on the count of `API_MEASURED` observations,
not on elapsed time.

## Guard rails

1. Experiments run within the same budget windows as normal production. An experiment is not a reason to
   exceed a cap.
2. Every variant passes the same QA, rights and disclosure gates. There is no experimental exemption from
   compliance.
3. A variant that fails compliance is excluded from the experiment rather than published with a waiver.
4. Results update `memory_records` with confidence derived from sample size and effect size, subject to
   the confidence floor in `SPEC/22`.

## Honest statistics

A concluded experiment records its sample size, the measurement window and the observed effect. It does
not record a conclusion beyond what the sample supports. A system that learns from underpowered
experiments will confidently adopt noise, and it will do so faster than a human would notice.
