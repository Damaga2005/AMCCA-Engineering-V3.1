# Changelog — V2 to V3

**Package version:** 3.0.0 · **Date:** 2026-09-02
**Supersedes:** AMCCA Engineering Specification V2 and AMCCA Blueprint V2.1, both withdrawn.

`AUDIT/V2_DEFECTS_CLOSED.md` holds the defect-by-defect trace with the executable check for each.
This file is the summary.

## Added

- `TOOLS/validate_package.py` — the executable release gate. 49 checks. A failing validator is a
  failing build, with no prose override (D-025, D-029).
- `TOOLS/conformance_tests.py` — 46 contract cases, 27 of them negative cases that must be rejected.
  A schema that has never rejected anything has not been shown to enforce anything.
- `SPEC/45_SYNTHETIC_CONTENT_DISCLOSURE.md` — the largest gap in V2. Sourced, dated, and marked as
  requiring re-verification.
- `SPEC/51_PRIVACY_DATA_PROTECTION.md` and `POLICIES/PRIVACY_POLICY.md` — personal data as a tracked class.
- `SPEC/65_OPENAPI_BOUNDARY.md` and `SCHEMAS/openapi.yaml` — the optional boundary, specified rather than
  referenced by a broken path.
- `SPEC/67`, `SPEC/68`, `SPEC/69` — concurrency model, clock discipline, diagnostics bundle.
- Six schemas that did not exist: `audit`, `tool-run`, `claim`, `rights`, `cost-event`, `config`.
- Decisions D-021 to D-030, each closing a named V2 defect.
- Invariants I-11 to I-22, each with a named enforcement mechanism and an adversarial test.

## Changed

- State machine rebuilt: 32 states, 198 transitions, generated from one canonical source. Producing
  states added so rework has a legal destination.
- Database contract rebuilt: 58 tables, all with columns. V2 declared 44 and defined 32.
- D-018 amended so the decision and `event.schema.json` agree.
- SPEC renumbered contiguously 01-83 with no duplicate numbers and no duplicate subjects.
- One Definition of Done, one testing strategy, one error model, one budget vocabulary.
- Money is a decimal string everywhere.
- `MANIFEST.md` excludes itself.

## Removed

- The second Blueprint.
- The self-certifying final audit.
- Unsourced assertions about the provider gateway's API.

## Known limitations

Stated in full at the end of `AUDIT/V2_DEFECTS_CLOSED.md`. In short: the gateway is unverified, platform
rules come from secondary sources, one AI Act transitional provision is uncertain, budget figures are
unvalidated against real pricing, nothing here is legal advice, and a green validator proves internal
consistency — not that the architecture is right.
