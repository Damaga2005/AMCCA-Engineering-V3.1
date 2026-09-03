# 75 — Acceptance Tests

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

Each scenario is end-to-end against fakes, with a deterministic clock, asserting observable outcomes
rather than internal calls.

| # | Scenario | Pass condition |
|---|---|---|
| A-01 | Full happy path from opportunity to `PUBLICATION_VERIFIED` in `MANUAL` mode | Every gate recorded; publication verified from authoritative evidence |
| A-02 | Same path with `dry_run = true` | Every stage runs; zero external mutations; no fabricated success |
| A-03 | Research yields insufficient sources | Production `BLOCKED` with `AMCCA-RES-001`; notification; no script generated |
| A-04 | A material claim is contradicted | Claim `DISPUTED`; script asserting it rejected |
| A-05 | An asset has unknown licence terms | Rights `YELLOW`; autonomous path blocked; approval path offered |
| A-06 | QA fails on audio | Targeted rework re-enters `AUDIO_GENERATION`, not `RESEARCHING`; render regenerated |
| A-07 | QA fails three times identically | Failure signature detected; production `FAILED`; evidence retained |
| A-08 | Content is realistic synthetic video; target requires a label | Label applied before dispatch; `platform_label_applied = 1` |
| A-09 | Same, but the target lacks `apply_synthetic_label` capability | Target blocked with `AMCCA-CMP-001`; other targets unaffected |
| A-10 | Two targets; one verifies, one fails | Production stays `PUBLICATION_PROCESSING`; operator notified; not promoted |
| A-11 | Upload times out after send | `UNKNOWN_EXTERNAL_STATE`; reconciliation finds the item; no second upload |
| A-12 | Referral link validated only by HTTP 200 | Cannot reach `ACTIVE`; publication blocked with `AMCCA-REF-001` |
| A-13 | Affiliate disclosure missing from script | `AMCCA-CMP-002` at `CONTENT_QA`; rework |
| A-14 | Daily budget exhausted mid-cycle | In-flight work completes; no new cycles; `PAUSE` notification |
| A-15 | Provider price changes between estimate and settlement | Settlement uses the actual usage and its snapshot; variance recorded |
| A-16 | Analytics returns an estimate after a measurement | Measurement still returned on read (I-12) |
| A-17 | Revenue import attempts an estimated provenance | Rejected by `CHECK` (I-13) |
| A-18 | Operator engages emergency stop mid-publication | Work halted; intent left `UNKNOWN`; state persists across restart |
| A-19 | Export then import a production into a clean instance | Hashes verified; imported as a copy; publications not claimed as verified |
| A-20 | Autonomous cycle end to end with every gate passing | Completes; every decision, cost and evidence item traceable from the inspector |

A-02 is worth emphasising. A dry run that silently fabricates a success response would make every other
test meaningless, because it would be testing a simulator.
