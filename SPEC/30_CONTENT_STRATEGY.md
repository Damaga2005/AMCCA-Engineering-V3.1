# 30 — Content Strategy

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Inputs

A selected opportunity, its niche, the verified claim set, the media profile for the intended platforms,
and memory records above the confidence floor.

## Outputs

A concept record: angle, target duration, platform targets, language, tone constraints, required
disclosures, and the claim subset the content will assert.

## Constraints applied before generation

1. Target duration must fit every target platform's media profile (`SPEC/34`); an unsatisfiable target
   set is rejected before any generation cost is incurred.
2. The asserted claim subset must be entirely `VERIFIED`. `ESTIMATED` and `DISPUTED` claims may appear
   only with explicit uncertainty wording; `UNKNOWN` claims may not appear.
3. Subject classes triggering the stricter evidence bar (people, health, finance, law, breaking events)
   propagate a tone and evidence constraint to the script stage.
4. Required affiliate disclosure and synthetic-content disclosure are determined here, not at publish
   time, so that they are designed into the content rather than bolted on.

Point 4 matters practically: a disclosure discovered at preflight forces a rework of a finished render,
which is the most expensive possible moment to discover it.

## Strategy versioning

The concept is an artifact version like any other, with lineage to the opportunity and claims it derives
from. Changing the concept after `CONCEPT_SELECTED` invalidates downstream artifacts through the DAG.
