# 32 — Script and Storyboard

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Script

The script agent proposes; deterministic validation decides. Validation checks:

| Check | Failure |
|---|---|
| Conforms to the script schema | `AMCCA-AI-003` |
| Every material factual line maps to a `VERIFIED` claim id | `AMCCA-RES-001` |
| No line asserts an `UNKNOWN` claim | Rejected |
| `ESTIMATED` and `DISPUTED` claims carry explicit uncertainty wording | Rejected |
| Content policy screen | `AMCCA-CMP-001` family |
| Required affiliate disclosure text is present and placed per platform rules | `AMCCA-CMP-002` |
| Estimated spoken duration within tolerance of the target | Rejected |
| No claim about an identified private individual without the stricter evidence bar | Rejected |

The claim-mapping requirement is the load-bearing one. Without it, a language model's fluency becomes
indistinguishable from evidence, which is the precise failure mode this whole architecture exists to
prevent.

## Storyboard

Structural validation:

- Every scene references a script segment; every script segment is covered by at least one scene.
- Scene durations sum to the script duration within tolerance.
- Each scene declares required asset kinds and any on-screen text.
- On-screen text respects the safe areas of every target media profile.
- Any scene carrying a required disclosure is marked so that QA can verify its presence and legibility in
  the final render.

## Versioning

Script and storyboard are artifact versions with DAG edges to the claims and concept they derive from.
A claim later downgraded from `VERIFIED` invalidates every script version asserting it, through the same
DAG mechanism that handles a failed render.
