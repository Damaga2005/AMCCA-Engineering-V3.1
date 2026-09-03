# AMCCA — Autonomous Multimodal Content Creation & Monetization Center

## V3 Engineering Specification — implementation-grade

**Package version:** 3.1.0
**Supersedes:** AMCCA Engineering Specification V2, AMCCA Blueprint V2.1, and AMCCA Engineering V3.0.0, all of which are withdrawn or superseded.
**Build target:** Windows 10/11 x64. Primary artifact `AMCCA.exe`; optional installer `AMCCA-Setup.exe`.

V1 established the product vision. V2 fixed the technology stack and the architectural boundaries, and did
that part well. V3 exists because an independent audit of V2 found that the *contracts* did not match the
*decisions*: the state machine had unreachable states and no exit from its own safety state, twelve declared
database tables had no column contract, six of nine schemas violated the versioning decision, and no schema
could link a QA result back to the production it belonged to.

V3 closes those gaps and adds the thing V2 lacked structurally: **the package validates itself.**
`TOOLS/validate_package.py` mechanically proves the invariants below and fails the build otherwise.

V3.1 exists because a second audit found that V3's validator, in several places, checked a weaker claim
than the one V3's prose documented — a real drift check that only looked for a marker comment, a
`format: date-time` declaration with no format checker wired in, a money pattern that admitted a sign it
shouldn't have, an evidence enum that let a resolving URL count as proof of publication. Every one of
those is closed in V3.1, each with an executable regression test named in `SPEC/79` and run by
`TOOLS/release_gate.py`. See `CHANGELOG_V3_TO_V3.1.md`.
See `AUDIT/V2_DEFECTS_CLOSED.md` for the defect-by-defect trace.

---

## Non-negotiable principles

1. Deterministic code controls state, money, permissions, files, hashes, budgets, credentials, retries and external side effects.
2. AI agents reason and propose structured outputs. They do not mutate protected state and do not decide whether a gate passed.
3. Unknown external state is never silently converted to success or failure.
4. Publishing is a side effect behind explicit capability, policy, credential, rights, disclosure and QA gates.
5. Every important decision and artifact is traceable, versioned and reproducible.
6. Autonomous mode is bounded by explicit policy, never by agent discretion.
7. The application recovers safely after restart, crash, timeout, network loss and ambiguous external responses.
8. No integration is simulated. An unsupported or unverified capability is disabled, not faked.
9. Estimates and measurements are different types and never overwrite one another.
10. A release gate is an executable check, not a prose claim.

## Source of truth order

1. `DECISIONS.md`
2. `BLUEPRINT/10_OPERATIONAL_INVARIANTS.md`
3. Other `BLUEPRINT/` documents — for questions of boundary and authority
4. Normative `SPEC/` documents — for questions of detail
5. `SCHEMAS/` JSON Schemas and `SCHEMAS/state-machine.json`
6. `POLICIES/`
7. `CONFIG/` examples and explanatory prose

If two documents conflict, implementation **MUST stop** and the conflict MUST be resolved in `DECISIONS.md`
before code changes continue. Do not choose. Choosing silently is how V2 acquired its defects.

Generated files (`SPEC/11`, `SPEC/13`, `SCHEMAS/*.json`, `MANIFEST.md`) are outputs, not inputs:
edit the generator, never the artifact (D-025).

## Canonical entry points

| Question | Document |
|---|---|
| What may I not change? | `DECISIONS.md` |
| What must always hold true? | `BLUEPRINT/10_OPERATIONAL_INVARIANTS.md` |
| What is the shape of the system? | `BLUEPRINT/00_MASTER_BLUEPRINT.md` |
| What states can a production be in? | `SPEC/12`, `SPEC/13` |
| What does the database look like? | `SPEC/10`, `SPEC/11` |
| What is the contract for a given aggregate? | the matching file in `SCHEMAS/` |
| When is a release done? | `SPEC/79_DEFINITION_OF_DONE.md` |
| In what order do I build it? | `BUILD_ORDER.md`, `SPEC/80` |
| Where do I start as an agent? | `ANTIGRAVITY_START_PROMPT.md` |

## Modes and environments

These are **two orthogonal axes**, not one list. Conflating them was a V2 defect.

**Environment** — `DEVELOPMENT`, `STAGING`, `PRODUCTION`. Selects configuration and which external
endpoints are reachable.

**Flags** — independent booleans that apply within any environment:

| Flag | Meaning |
|---|---|
| `publishing_enabled` | Whether publication intents may be dispatched at all. |
| `dry_run` | When true, every tool of class `EXTERNAL_UNSAFE` is blocked. Planning, research, generation and QA still run fully. |
| `autonomy_mode` | `MANUAL`, `ASSISTED` or `AUTONOMOUS`. Governs which actions need approval. |

`STAGING` is the only environment where `publishing_enabled=true` may be combined with `dry_run=false`
against non-production platform accounts, and only when `providers.gateway.capabilities_verified` is true.
`CONFIG/environments.yaml` states this explicitly rather than leaving it to be inferred.

## Package self-validation

```
pip install -r TOOLS/requirements.txt     # pinned, exact dependency versions
python TOOLS/release_gate.py              # THE canonical release-readiness command -- run this
```

`TOOLS/release_gate.py` is the single entry point: it orchestrates every check below as a
named, numbered step and is what decides release-readiness. Every script it calls remains
individually runnable too, for CI granularity and local debugging while working on one area:

```
python TOOLS/validate_package.py          # structural, contract and drift checks only
python TOOLS/validate_package.py --regen  # regenerate derived artifacts, then verify
python TOOLS/generate_artifacts.py --check    # drift check in isolation (byte-for-byte, V31-01)
python TOOLS/conformance_tests.py             # schema conditional coverage + positive/negative cases
python TOOLS/test_version_consistency.py          # no stale version string in a normative location
python TOOLS/test_generated_artifacts_semantics.py # semantic validation beyond the byte-level diff
python TOOLS/test_database_contract_source.py      # DDL-consuming tests load the real, generated DDL
python TOOLS/test_no_contract_duplication.py       # no test hand-copies a CREATE TABLE / CHECK
python TOOLS/test_mutations.py                     # deliberately break each invariant, prove it goes red
```

Every `TOOLS/test_*.py` is a standalone script (`run()` returning an exit code, guarded by
`if __name__ == "__main__"`), not a pytest test module -- run them directly, or via
`TOOLS/release_gate.py`, rather than `pytest TOOLS/`.

The validator proves, mechanically:

- every state has an inbound transition, every non-terminal state an outbound one, no terminal state an outbound one;
- every state is reachable from `INIT` and can reach a terminal state;
- every JSON Schema is a valid draft 2020-12 schema and carries `schema_version`;
- `format: date-time` is actually enforced by a registered format checker, not merely declared (V31-02);
- the production state enum matches `state-machine.json` exactly;
- every table named anywhere in the package has a column contract in `SPEC/11`;
- money fields are non-negative except the two explicitly signed exceptions, and no tooling code uses `float` for money (V31-04, V31-05);
- a publication cannot reach `VERIFIED` without authoritative evidence, and the synthetic-label gate holds structurally, not only procedurally (V31-06, V31-07);
- a platform capability discovered via a secondary source can never reach `VERIFIED` (V31-09);
- every internal file reference resolves;
- every SPEC number is unique and every SPEC file is referenced by the traceability map;
- `MANIFEST.md` matches the tree, and no file claims to contain its own hash;
- generated artifacts have not drifted from their generators, verified byte-for-byte, not by a marker comment (V31-01).

`TOOLS/release_gate.py` runs the full sequence in the order fixed by the V3.1 / V3.1.1 audits and
reports each step by name; see `SPEC/79` for which criterion each step demonstrates, and note that
three of its steps (security, chaos and acceptance suites) require a running implementation and are
honestly reported as not applicable at specification stage rather than claimed passing (V31-10). Steps
21-25 are the V3.1.1 validation-hardening additions: version consistency, generated-artifact semantics,
database-contract single-sourcing, the anti-duplication guard, and the mutation-test suite.

A failing validator is a failing build. There is no prose override.
