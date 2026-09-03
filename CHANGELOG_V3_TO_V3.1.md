# Changelog — V3.0.0 to V3.1.0

**Package version:** 3.1.0 · **Date:** 2026-09-03
**Basis:** independent audit of V3.0.0 (`AMCCA-Engineering-V3_1-Auditoria-Correcciones.md`), applied in
full. All ten findings (V31-01 through V31-10) are closed and covered by an executable check.

This is a corrective release. Per the audit's own scope, **no product functionality was added.** Every
change either closes a validation gap, tightens a contract that was looser than its own stated invariant,
or makes a guarantee executable that was previously only documented.

## Summary

| ID | Finding | Closed by |
|---|---|---|
| V31-01 | `--regen` didn't prove artifacts hadn't drifted | `TOOLS/generate_artifacts.py --check`, byte-for-byte diff |
| V31-02 | `format: date-time` was declared but not enforced (`FormatChecker` needs `rfc3339-validator`, confirmed empirically) | `rfc3339-validator` pinned; every validator construction uses `format_checker=` |
| V31-03 | New `if`/`then` conditionals could ship without test coverage | `TOOLS/conditional_coverage.json` + automatic discovery in `conformance_tests.py` |
| V31-04 | Money pattern admitted an unconditional sign | `NonNegativeMoney` / `SignedMoney` split, schema + database |
| V31-05 | The validator itself used `float` for money | `Decimal` throughout; static AST guard against `float()` in `TOOLS/*.py` |
| V31-06 | A resolving-URL check could satisfy `VERIFIED` | Renamed to `POST_PUBLISH_CHECK`; excluded from the authoritative-evidence enum |
| V31-07 | Synthetic-label gate depended on the preflight code path | `publications.synthetic_declaration_id` FK + structural schema/DB conditional |
| V31-08 | AI Act obligations weren't split by responsible party | Responsibility matrix added to `SPEC/45` (provider / deployer / platform / AMCCA) |
| V31-09 | A secondary source could imply a verified capability | `DISCOVERED` status added; `CHECK` restricts `VERIFIED` to authoritative sources |
| V31-10 | Some documented guarantees weren't actually checked | `TOOLS/release_gate.py` names, per step, the check that proves it or states N/A |

## Added

- `TOOLS/generate_artifacts.py` — single canonical generator (state machine, schemas, database, traceability) with real `--check`/`--regen`.
- `TOOLS/conditional_coverage.json` — declared coverage map for every schema conditional.
- `TOOLS/release_gate.py` — runs the audit's mandated 20-step sequence.
- `TOOLS/requirements.txt` — pins `rfc3339-validator`, without which date-time validation silently no-ops.
- Seven regression scripts: `test_generation.py`, `test_schema_formats.py`, `test_money_precision.py`, `test_conditional_coverage.py`, `test_publication_evidence.py`, `test_synthetic_disclosure.py`, `test_platform_evidence.py`.
- `DECISIONS.md`: D-031 (money types), D-032 (format enforcement), D-033 (discovered vs verified evidence). D-025 and D-026 amended.
- `SPEC/45`: AI Act responsibility matrix.
- `SPEC/42`: `DISCOVERED` capability status.
- `SPEC/79`: criteria 10–16, and an explicit statement of what this document does not claim (V31-10).

## Changed

- `publication.schema.json`: `evidence_source` split into authoritative and non-authoritative subsets; `VERIFIED` requires the former. New required field `platform_label_required`; new field `synthetic_declaration_id`; second `allOf` conditional making the label gate structural.
- `cost-event.schema.json`, `job.schema.json`, `config.schema.json`: money fields use `NonNegativeMoney`; `cost-event.amount` uses `SignedMoney` with a conditional restricting non-`ADJUSTMENT` kinds to non-negative.
- `referral.schema.json`: `DISCOVERED` added to `state` enum.
- `SPEC/11_DATABASE_SCHEMA.md`: `publications` gains `synthetic_declaration_id`, `platform_label_required`, and their `CHECK` constraints; `platform_capabilities` gains `DISCOVERED` and an evidence-source `CHECK`; `budgets`, `budget_reservations`, `opportunities`, `cost_events`, `revenue_events` gain explicit non-negative `CHECK`s.
- `TOOLS/validate_package.py`: `Decimal` instead of `float`; real drift check via `generate_artifacts.check_all()`; `FormatChecker` on every schema validation; static anti-`float` AST check.
- Package version 3.0.0 → 3.1.0 throughout (`schema_version` const, `CONFIG/*.yaml`, `README.md`), because the contract changes above are major per `SPEC/58`'s own versioning rules.

## Known limitations carried forward

Everything listed at the end of `AUDIT/V2_DEFECTS_CLOSED.md` still applies (gateway identity unverified,
platform rules from secondary sources, one AI Act transitional detail uncertain, budget figures
unvalidated against real pricing, nothing here is legal advice). V3.1 adds one more, named honestly rather
than hidden: `TOOLS/release_gate.py` steps 17, 19 and 20 (security, chaos and acceptance suites) require a
running implementation to execute against, and are reported as not applicable at specification stage —
never claimed as passing.
