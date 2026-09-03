# Build Order

Each phase must compile, have tests, and pass `TOOLS/validate_package.py` before the next begins.
The ordering is not arbitrary: each phase depends on a guarantee the previous phase established.

| # | Phase | Establishes | Exit criterion |
|---|---|---|---|
| 1 | Repository, CI, package validator | The spec cannot silently drift | `validate_package.py` runs green in CI on every commit |
| 2 | Configuration, secrets, preflight | Nothing starts misconfigured | Invalid config aborts startup; a literal secret aborts startup |
| 3 | Database, migrations, event store, audit store | Durable, versioned, append-only history | Upgrade, rollback and restore tests pass |
| 4 | Domain model and state machine | Illegal transitions are impossible | Every transition in `SPEC/13` has a test; every non-listed transition is rejected |
| 5 | Jobs, leases, idempotency, recovery, reconciliation | Work survives crashes without duplicating | Kill -9 at every checkpoint leaves a consistent, resumable state |
| 6 | Tool registry and agent runtime | Agents cannot exceed their contract | An agent calling a forbidden tool is blocked and audited |
| 7 | Provider gateway port + first adapter + model registry | AI capability behind an abstraction | A model cannot be enabled without a successful capability probe |
| 8 | Research, claims, sources, trends, opportunity scoring | Evidence plane exists | A material claim without sufficient sources cannot reach VERIFIED |
| 9 | Script, storyboard, assets, voice, render | Production pipeline produces artifacts with lineage | The artifact DAG is complete and acyclic for a full run |
| 10 | Deterministic QA, AI-assisted QA, rights, duplicates | Quality gates that AI cannot self-certify | A PASS verdict is unreachable using AI findings alone |
| 11 | Rework and DAG invalidation | Failure has a defined path | Every QA stage failure produces a targeted, bounded rework |
| 12 | Platform hub, OAuth, capability matrix, publishing | Publication with verification, not optimism | No duplicate publication under chaos testing |
| 13 | Synthetic-content disclosure and compliance gate | Lawful, labelled publication | A required label that is unapplied blocks the intent |
| 14 | Monetization, attribution, analytics, revenue | Money that is measured, not guessed | An estimate cannot enter the revenue ledger |
| 15 | Memory, genome, experiments | Learning from measured outcomes | A niche reaches PROVEN only from measured data |
| 16 | Desktop UI and inspectors | The operator can see and stop everything | Every gate and every blocked item is visible and explains itself |
| 17 | Chaos, concurrency, security suites | The guarantees are real, not aspirational | All suites in `SPEC/72`, `SPEC/73`, `SPEC/74` pass |
| 18 | Packaging, installer, signing, release validation | A shippable artifact | Clean install, upgrade, uninstall-preserve and restore all verified |

**Autonomous publishing is not enabled before phases 1-17 pass.** Phase 18 does not unlock it either;
enabling it is a separate, explicit, audited operator decision (D-020, D-015).
