# Synthetic Content Policy

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

> The engineering specification is `SPEC/45`, which carries the sourced regulatory and platform facts
> with their retrieval dates. This file states the policy position.

## Position

AMCCA publishes AI-generated content. It does so **labelled**, wherever labelling is required by law,
platform terms or honest practice.

## Responsibility is split, not uniform

Not every obligation touching AI-generated content is AMCCA's to discharge. The full responsibility
matrix — separating what the model provider owes, what the deployer (operator) owes, what the platform
mechanism handles, and what AMCCA's own internal control enforces — lives in `SPEC/45` (V31-08). The one
row AMCCA treats as a hard, non-negotiable gate is the deployer disclosure; C2PA provenance is tracked as
a SHOULD, propagated when available, and is never treated as a substitute for that disclosure.

## Non-negotiable

Skipping a required synthetic-content label is blocked in every autonomy mode, including `MANUAL`, and
there is no approval that grants it (`BLUEPRINT/05`). An operator who wants to publish unlabelled
synthetic content must do so outside this system.

## Determination

The set of AI-generated components is **derived from the artifact DAG**, from
`artifact_versions.generator_model_id`, not declared by an agent. The system knows what it generated
because it recorded it at the moment of generation. This removes the possibility of understatement.

## Threshold

The consistent test across the target platforms is whether the content **realistically depicts** people,
voices, places or events. AI assistance with scripting, captions, translation, colour grading, noise
removal and reframing does not by itself trigger a label. Realistic synthetic depiction does.

## Identifiable people

A realistic synthetic depiction of a real, identifiable person requires operator approval regardless of
autonomy mode, and is subject to the `PERSON` evidence bar in `CONTENT_POLICY.md`. This is the case where
an autonomous system's judgement is worth least.

## Machine-readable provenance

C2PA Content Credentials SHOULD be embedded where the pipeline can produce them, in addition to the
platform-native label. They serve the machine-readable marking direction of the EU AI Act Article 50(2)
and are independently useful as provenance.

## Re-verification

The specific duties change. `CONFIG/platforms.yaml` carries `source_ref` and `retrieved_at` per rule set,
and a stale rule set degrades the platform capability to `UNVERIFIED`, which blocks autonomous
publication to it.
