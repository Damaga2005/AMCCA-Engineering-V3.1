# 71 — Test Matrix

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Invariant coverage

| Invariant | Adversarial test |
|---|---|
| I-01 one committed state | Concurrent transition attempts on one production; exactly one succeeds |
| I-02 event per transition | Transition committed with event insert forced to fail; assert full rollback |
| I-03 intent before effect | Adapter called with no intent row; assert refusal and `CHECK` violation |
| I-04 unknown stays unknown | Timeout after send; assert `UNKNOWN`, assert no retry, assert no second intent |
| I-05 one active lease | Two workers claim simultaneously; assert one wins and the loser's fence token is stale |
| I-06 budget not exceeded | N concurrent reservations against a limit of N-1; assert exactly N-1 succeed |
| I-07 sealed manifests immutable | Attempt to modify a sealed version; assert refusal |
| I-08 tombstones auditable | Delete an artifact; assert the row survives with metadata |
| I-09 agents cannot mutate | Agent attempts a protected write; assert no path exists and the audit enum has no `AGENT` |
| I-10 policy block terminal | Retry a blocked action without a policy change; assert the same block |
| I-11 verified needs evidence | Insert a `VERIFIED` publication with no evidence; assert `CHECK` violation |
| I-12 estimate does not overwrite | Ingest a measurement then an estimate for the same window; assert the read returns the measurement |
| I-13 estimates out of the ledger | Insert a revenue event with `ESTIMATED` provenance; assert `CHECK` violation |
| I-14 no secrets leak | Seed known markers; run logs, export and diagnostics bundle; assert zero occurrences |
| I-15 no self-elevation | Agent and scheduler attempt to raise autonomy; assert refusal and audit |
| I-16 emergency stop persists | Engage, kill process, restart; assert still engaged |
| I-17 no duplicate publication | Concurrent dispatch plus a forced lock failure; assert the unique constraint catches it |
| I-18 no unlabelled synthetic | Required label unapplied; assert preflight block with `AMCCA-CMP-001` |
| I-19 no AI-only PASS | QA report with only AI-assisted findings; assert `PASS` unreachable |
| I-20 state machine well-formed | `TOOLS/validate_package.py` in CI |
| I-21 every table has a contract | `TOOLS/validate_package.py` in CI |
| I-22 no call in a transaction | Transaction wall-clock ceiling assertion across the suite |

## Additional required coverage

| Area | Requirement |
|---|---|
| State machine | Every transition in `SPEC/13` has a positive test; a sample of non-listed transitions is rejected with `AMCCA-STM-001` |
| Error catalogue | Every code in `SPEC/05` has a test that produces it |
| Schemas | Every conditional `allOf` has a negative instance that must fail validation |
| Migrations | Every migration has a forward and a rollback test |
| Adapters | Every adapter passes the shared contract suite |
| Recovery | Kill at every checkpoint in `SPEC/16`; assert consistency and resumability |
| Money | Decimal arithmetic across the full budget lifecycle; assert no float appears in any money path |
