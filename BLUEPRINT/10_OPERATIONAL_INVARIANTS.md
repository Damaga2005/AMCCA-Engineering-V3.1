# Operational Invariants

> **Normative.** These override any conflicting SPEC text (D-021). Each invariant names the mechanism
> that enforces it, because an invariant enforced only by discipline is not an invariant.
> Each has an adversarial test in `SPEC/73` or `SPEC/74`.

| # | Invariant | Enforced by |
|---|---|---|
| I-01 | A production has exactly one committed current state. | `productions.state` single column; every write inside TX-1 |
| I-02 | Every committed state transition emits an event and a `state_transitions` row naming a transition from `SPEC/13`. | TX-1 atomicity; `CHECK` on `transition_id` |
| I-03 | Every external mutation has a persisted, committed intent before the call. | `tool_runs` CHECK on `side_effect_class`; TX-5 ordering |
| I-04 | An ambiguous external mutation is `UNKNOWN` until reconciled, never success or failure. | `intents.state` enum; no code path writes `CONFIRMED` without evidence |
| I-05 | A job has at most one active lease. | `leases` PK on `job_id`; fence tokens; single conditional UPDATE claiming |
| I-06 | A budget reservation cannot exceed the available budget, under concurrency. | Limit check inside the `WHERE` clause of one UPDATE |
| I-07 | Artifact versions are immutable once sealed into a manifest. | `artifact_manifests.sealed`; version rows immutable except `state` |
| I-08 | Deleted artifacts remain auditable through tombstone metadata. | `artifact_versions.state = TOMBSTONED`; row retained |
| I-09 | Agents cannot mutate protected aggregates. | No database handle in agent runtime; `audit_log` has no `AGENT` actor |
| I-10 | A policy `BLOCK` is terminal for the attempted action until an authorised operation changes policy state. | `policy_decisions` row; `productions.blocked_from` |
| I-11 | A publication cannot be `VERIFIED` without authoritative evidence. | `publications` CHECK constraint; `publication.schema.json` conditional restricted to `OFFICIAL_API`/`OFFICIAL_DASHBOARD`/`OPERATOR_CONFIRMATION` (V31-06: `POST_PUBLISH_CHECK`, a resolving-URL check, is explicitly excluded) |
| I-12 | An estimated metric never overwrites a measured one. | `analytics_snapshots` unique key includes provenance; read path prefers `API_MEASURED` |
| I-13 | An estimate can never enter the revenue ledger. | `revenue_events` CHECK forbids `ESTIMATED` provenance |
| I-14 | Secrets never appear in logs, exports or the database. | Redaction middleware; `secret://` CHECK constraints; security tests |
| I-15 | Autonomous publishing cannot be enabled by any non-operator actor. | Autonomy matrix; `audit_log` actor types; approval scope |
| I-16 | Emergency stop survives application restart. | `kill_switch_state` single-row table read during startup |
| I-17 | No duplicate publication for the same content version and target. | `UNIQUE(production_id, platform, account_id, content_version_id)` |
| I-18 | Content requiring a synthetic-content label is not published without it. | `publications` CHECK constraint (`state='VERIFIED'` requires `synthetic_label_applied=1` when `platform_label_required=1`), plus `publications.synthetic_declaration_id` FK linkage and `publication.schema.json` conditional; `synthetic_declarations` CHECK; preflight gate in `SPEC/49`. Made structural rather than purely procedural by V31-07: the gate now holds even if the preflight code path has a bug, because the contract itself refuses the object. |
| I-19 | A QA `PASS` verdict is never produced by AI-assisted findings alone. | `verdict` computed deterministically; `check_kind` discriminator |
| I-20 | Every state is reachable from `INIT` and can reach a terminal state. | `TOOLS/validate_package.py`, run in CI |
| I-21 | Every table referenced anywhere has a column contract. | `TOOLS/validate_package.py`, run in CI |
| I-22 | No external call occurs inside a database transaction. | Transaction boundary list in `SPEC/11`; concurrency test asserts it |

## How to read this table

The middle column is the promise. The right-hand column is why you should believe it.
If a future change makes the right-hand column false, the invariant has been silently removed even if the
text still reads the same way — which is the specific failure this package was rebuilt to prevent.
