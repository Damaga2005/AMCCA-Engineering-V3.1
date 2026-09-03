# Content Policy

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Evidence bar

| Subject class | Bar |
|---|---|
| `GENERAL` | `policy.research.min_sources` independent sources for every material claim |
| `BREAKING_EVENT` | Stricter recency window; `PRIMARY` or `SECONDARY` tier only; aggregators cannot be sole support |
| `PERSON` | Claims about identified individuals require `PRIMARY` sourcing; no autonomous assertion |
| `HEALTH` | `PRIMARY` sourcing; no instruction, dosage or diagnostic framing; no autonomous assertion |
| `FINANCE` | `PRIMARY` sourcing; no personalised recommendation framing; no autonomous assertion |
| `LEGAL` | `PRIMARY` sourcing; no advice framing; no autonomous assertion |

"No autonomous assertion" means the content may discuss the subject, but a claim in these classes cannot
pass `CONTENT_QA` in `AUTONOMOUS` mode without an operator approval recorded.

## Prohibited

Content that: asserts an `UNKNOWN` claim as fact; presents an `ESTIMATED` or `DISPUTED` claim without its
uncertainty; realistically depicts an identifiable private individual without approval; impersonates a
real person or organisation; targets a protected characteristic; or promises an outcome the content does
not substantiate.

## Uncertainty wording

`ESTIMATED` and `DISPUTED` claims must carry explicit uncertainty in the delivered content, not only in
the database. The script validator checks for the presence of a hedging construction adjacent to the
claim reference; the QA content stage checks that the delivered audio and captions preserve it.

## Hook honesty

Every factual assertion in a hook must map to a claim the body of the content also asserts. A hook that
promises what the content does not deliver fails deterministically, not by editorial judgement.
