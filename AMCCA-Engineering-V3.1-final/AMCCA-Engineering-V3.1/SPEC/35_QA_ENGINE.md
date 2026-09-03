# 35 — QA Engine

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## The rule that governs everything else

A QA **verdict** is computed by deterministic code from scores and findings. An AI-assisted check produces
evidence only. A `PASS` is unreachable from AI findings alone (D-024, I-19). Attempting to set a verdict
from an AI-assisted finding raises `AMCCA-QA-002`.

`qa.schema.json` carries a `check_kind` discriminator on every finding precisely so this distinction
survives serialisation, storage and export.

## Stages

| Stage | Deterministic checks | AI-assisted evidence |
|---|---|---|
| `TECHNICAL_QA` | Container, codec, duration, resolution, frame rate, decode integrity, manifest consistency | — |
| `VISUAL_QA` | Black frames, freeze frames, safe areas, resolution consistency | Visual coherence, artefact detection |
| `AUDIO_QA` | Silence, clipping, loudness, A/V sync, caption timing | Intelligibility, prosody |
| `CONTENT_QA` | Claim mapping, disclosure presence, prohibited-term screen | Tone, factual review assistance |
| `RETENTION_QA` | Hook presence, pacing metrics | Retention heuristics |
| `COMPLIANCE_QA` | Rights all GREEN, affiliate disclosure present, synthetic label declared, platform policy screen | — |

`TECHNICAL_QA` and `COMPLIANCE_QA` have no AI column at all. Those two stages decide whether the file is
valid and whether publishing it is lawful, and neither question is answered by an opinion.

## Thresholds

`overall_score >= policy.qa.overall_min` (default 8.5) **and** every critical dimension
`>= policy.qa.critical_min` (default 8.0). Critical dimensions are a fixed set:
`factual_accuracy`, `rights`, `technical_integrity`, `audio_intelligibility`, `visual_integrity`.

The set is fixed in the schema rather than free-form so that a threshold rule cannot be evaded by renaming
a dimension. A stricter platform profile may raise thresholds; nothing may lower them.

## Findings

Every finding carries `check_id`, `check_kind`, `status`, `severity`, `responsible_artifact_version_id`,
and where applicable expected, actual, scene reference, timecode, evidence reference and a remediation
code.

`responsible_artifact_version_id` is mandatory. Without it, rework has no target and the system can only
regenerate everything — which is precisely what `SPEC/37` exists to avoid.

## Outcome

`verdict = PASS` requires: no `CRITICAL` finding, no `HIGH` finding above the configured allowance, all
thresholds met, and every deterministic check either `PASS` or `WARN`. Anything else is `FAIL` and routes
to rework.
