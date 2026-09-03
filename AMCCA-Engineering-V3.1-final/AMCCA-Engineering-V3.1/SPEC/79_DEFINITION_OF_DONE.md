# 79 — Definition of Done

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

> This is the **single** Definition of Done. V2 shipped two with different criteria, which meant it had
> none (D-022). Every criterion below is demonstrated by an executable check, never by assertion (D-029).

## Package-level

| # | Criterion | Demonstrated by |
|---|---|---|
| 1 | Every schema is valid draft 2020-12 and carries `schema_version` | `validate_package.py` |
| 2 | The production state enum matches `state-machine.json` | `validate_package.py` |
| 3 | Generated artifacts have not drifted from their generators | `validate_package.py --regen` diff |
| 4 | The state machine satisfies all seven structural guarantees in `SPEC/13` | `validate_package.py` |
| 5 | Every table named anywhere has a column contract in `SPEC/11` | `validate_package.py` |
| 6 | Every internal file reference resolves | `validate_package.py` |
| 7 | SPEC numbers are unique; every SPEC file appears in the traceability map | `validate_package.py` |
| 8 | `MANIFEST.md` matches the tree and excludes itself | `validate_package.py` |
| 9 | Every schema conditional rejects its negative instance | `conformance_tests.py` |
| 10 | Every JSON Schema format constraint (`format: date-time`) is actually enforced by a registered format checker, not merely declared | `TOOLS/test_schema_formats.py`, `validate_package.py` (`formats.*`) |
| 11 | Every discovered `if`/`then` conditional has declared positive and negative test coverage; an undeclared conditional fails the build | `TOOLS/test_conditional_coverage.py`, `conformance_tests.py` |
| 12 | Generated artifacts are produced deterministically from `TOOLS/generate_artifacts.py` and are byte-for-byte drift-free against the checked-in files | `TOOLS/generate_artifacts.py --check`, `TOOLS/test_generation.py` |
| 13 | Money is represented as `NonNegativeMoney` by default; only `cost_events(ADJUSTMENT)` and `revenue_events(REVERSED)` may be signed; no tooling code calls `float()` on a monetary value | `TOOLS/test_money_precision.py` (static AST check + Decimal precision test) |
| 14 | A publication cannot reach `VERIFIED` without authoritative evidence (`OFFICIAL_API`, `OFFICIAL_DASHBOARD`, `OPERATOR_CONFIRMATION`); a resolving-URL check alone is insufficient | `TOOLS/test_publication_evidence.py` |
| 15 | The synthetic-content label gate survives even if the preflight code path that is supposed to enforce it has a bug, because it is a database and schema constraint, not only a procedural check | `TOOLS/test_synthetic_disclosure.py` |
| 16 | A capability discovered via a secondary source can never reach `VERIFIED` status; only authoritative evidence sources can | `TOOLS/test_platform_evidence.py` |

## Implementation-level

| # | Criterion | Demonstrated by |
|---|---|---|
| 10 | Every transition in `SPEC/13` has a passing test; non-listed transitions are rejected | Test suite |
| 11 | Every invariant in `BLUEPRINT/10` has a passing adversarial test | `SPEC/71` matrix |
| 12 | Every error code in `SPEC/05` is produced by at least one test | Test suite |
| 13 | Every adapter passes the shared contract suite including timeout-after-send | Adapter suite |
| 14 | Chaos scenarios X-01 to X-16 pass | `SPEC/74` |
| 15 | Concurrency scenarios C-01 to C-14 pass | `SPEC/73` |
| 16 | Security scenarios S-01 to S-20 pass | `SPEC/72` |
| 17 | Acceptance scenarios A-01 to A-20 pass | `SPEC/75` |
| 18 | Migrations apply forward and roll back | Migration tests |
| 19 | Clean install, upgrade, uninstall-preserve and restore verified | Packaging tests |
| 20 | No floating-point type appears in any money path | Static check plus test |

## Release-level

| # | Criterion | Demonstrated by |
|---|---|---|
| 21 | Dependency advisories below threshold | Scan output |
| 22 | Release manifest generated, complete and signed | Release tooling |
| 23 | Known limitations documented | Changelog |

## What is explicitly not sufficient

It compiles. The happy path works. A demo published a video. An audit document says it is ready.
All four of those were true of V2, and V2 was not ready.

## What this document does not claim (V31-10)

A guarantee is listed above only where `TOOLS/release_gate.py` has a step that actually demonstrates it.
Criteria 17, 19 and 20 in `TOOLS/release_gate.py` (security, chaos and acceptance test suites, specified
in full in `SPEC/72`, `SPEC/74` and `SPEC/75`) run against a built application. This package ships a
specification, not a running application, so those suites are reported as **not applicable at
specification stage** rather than silently skipped or claimed passing. When an implementation exists,
those suites become executable gates in their own right and this document's implementation-level table
(criteria 10–20 there) already names them as such.

The general rule, stated once so it governs every claim in this file: **if the tooling cannot demonstrate
a guarantee, the guarantee is not declared as automatically verified.** A criterion without a named,
runnable check does not belong in this document; it belongs in the known-limitations list in
`AUDIT/V2_DEFECTS_CLOSED.md` instead.
