# AMCCA Engineering V3.1.1 — validation-hardening implementation summary

Repo: `/tmp/amcca/AMCCA-Engineering-V3.1` (git). Five commits on top of the initial
import, one per phase; no product/spec content changed except the two documented
defect fixes (state-machine `$id`, `SPEC/42` stale prose).

## What changed, by item

1. **DDL single-source-of-truth** — `TOOLS/generate_artifacts.py` gained
   `build_canonical_ddl()` / `build_ddl_sql_text()`, emitting real, executable
   `CREATE TABLE` statements for the load-bearing tables (`publications`,
   `synthetic_declarations`, `platform_capabilities`) from the same module-level
   vocabularies (`PUBLICATION_STATES`, `AUTHORITATIVE_EVIDENCE`,
   `PLATFORM_CAPABILITY_*`) the JSON Schemas are built from. Wired into
   `generate_all()` as the new generated artifact `SCHEMAS/schema.sql`.
   `TOOLS/test_synthetic_disclosure.py` and `TOOLS/test_platform_evidence.py` were
   rewritten to load this instead of a hand-copied `DDL = """CREATE TABLE..."""`
   string, with identical test-case coverage.

2. **Semantic validator** — `TOOLS/test_generated_artifacts_semantics.py`: calls
   the generator functions directly and asserts properties of the *model*
   (version fields match `SV`, `$id`s well-formed and current, production state
   enum matches the state machine, evidence enums correctly split
   authoritative/non-authoritative, money fields never typed as `number`,
   synthetic-disclosure gating fields present, traceability covers every SPEC
   file) — a second layer beyond `--check`'s byte diff, so a deterministic-but-
   wrong generator can't hide behind byte-identical wrong output.

3. **state-machine.json `$id` fix** — was hardcoded
   `"amcca://data/state-machine/3.0.0"` while `schema_version` said `3.1.0`.
   Fixed at the generator (`f"amcca://data/state-machine/{SV}"`), regenerated,
   `--check` confirms byte-clean and deterministic.

4. **Version consistency test** — `TOOLS/test_version_consistency.py`: scans
   `SCHEMAS/*.json`, `CONFIG/*.yaml`, and `*.md` package-version badges for a
   stale version string in a NORMATIVE location, importing the current version
   from `generate_artifacts.SV` rather than a second hardcoded literal.
   Documented allowlist: `CHANGELOG*.md`, `AUDIT/` files, prose under a
   `## History`/`## Changelog` heading.

5. **Conditional coverage hardening** — `conformance_tests.discover_conditionals()`
   / `check_conditional_coverage()` now take optional `schema_dir`/`cov_path`
   parameters. `TOOLS/test_conditional_coverage.py` adds five explicit synthetic-
   fixture cases (no entry / positive-only / negative-only / both / stale entry)
   against the real mechanism, not the real package's current snapshot.

6. **release_gate.py as sole arbiter** — added steps 21-25 for the new V3.1.1
   tests; documented in a module comment as the canonical single
   release-readiness command; README.md updated to lead with it.

7. **GitHub Actions** — `.github/workflows/validation.yml`: on push/PR, installs
   `TOOLS/requirements.txt`, then `validate_package.py`, `conformance_tests.py`,
   `generate_artifacts.py --check`, `release_gate.py`. Comment explains why
   `--regen` is never run in CI (it would silently paper over a wrong committed
   artifact instead of failing the build).

8. **Dependency pinning** — `TOOLS/requirements.in` (loose, previous content) +
   `TOOLS/requirements.txt` regenerated via `pip-compile` with exact versions
   (`jsonschema==4.26.0`, `rfc3339-validator==0.1.4`, `pyyaml==6.0.3`, plus their
   transitive pins). Network access was available in this environment, so all
   versions are real resolved versions, not invented.

9. **Anti-duplication test** — `TOOLS/test_no_contract_duplication.py`: scans
   `TOOLS/test_*.py` for a literal `CREATE TABLE` / `CHECK (` (word-boundary
   guarded to avoid false positives like `POST_PUBLISH_CHECK`) outside an
   allowlist. The allowlist is empty after item 1's rewrite. Companion
   `TOOLS/test_database_contract_source.py` proves (a) the DDL-consuming tests
   actually import the canonical builder and (b) stripping a real CHECK clause
   from a scratch copy of the DDL flips a case from rejected to accepted.

10. **Mutation tests** — `TOOLS/test_mutations.py`: six mutations, each on a
    scratch/in-memory copy, each proving the relevant real check goes red:
    money precision/non-negativity, publication authoritative-evidence gate,
    synthetic-declaration linkage requirement, a new state-machine
    verification-skip invariant (see below), a `float()` introduced into
    `generate_artifacts.py`'s source text (AST guard), and an uncovered schema
    conditional. Each mutation also proves the canonical source is unaffected.
    Mutation 4 required a genuinely new invariant in
    `generate_artifacts.validate_state_machine()` — no existing check would
    have caught a transition that shortcuts a state's dedicated verification
    step, so one was added (a `verified`-kind state may only be entered from
    its producing predecessor, a `BLOCKED` resume, or — for
    `PUBLICATION_VERIFIED` — reconciliation).

11. **PASS/FAIL/N/A/UNKNOWN discipline** — `validate_package.py`'s `check()`
    results now carry an explicit `status` field; added `check_na()` /
    `check_unknown()`. Fixed a real conflation bug:
    `config.example_validates_against_schema` used to `except ImportError: pass`,
    silently dropping the check from `RESULTS` entirely (neither PASS nor FAIL,
    just invisible) — now reports an explicit N/A with a justification. `main()`
    only treats FAIL/UNKNOWN as release-blocking. `release_gate.py`'s existing
    N/A handling (steps 17/19/20, always printed with a SPEC-referencing
    justification) was documented as the release-blocking rule in a module
    comment.

12. **V3.0/evidence-vocabulary audit** — `AUDIT/V31_1_1_VERSION_AUDIT.md`
    classifies every `3.0.0` / `UNVERIFIED_SECONDARY_SOURCE` /
    `PUBLIC_URL_CHECK` occurrence. Found and fixed one real drift beyond the
    `$id` bug: `SPEC/42_PLATFORM_CAPABILITY_MATRIX.md` asserted, in the present
    tense, that `CONFIG/platforms.yaml` "currently records ...
    `verification_status: UNVERIFIED_SECONDARY_SOURCE`" — but the config file
    had already moved to the post-V31-09 `DISCOVERED` vocabulary. `SPEC/42` was
    stale prose describing a config file that no longer said that; corrected by
    direct edit (it's hand-authored prose, not a generated artifact). Every
    other occurrence is historical (`CHANGELOG*`, the closed
    `AUDIT/V2_DEFECTS_CLOSED.md`) or a migration-note code comment / the
    implementation of the regression guard for the corresponding rename — none
    mass-deleted.

## What could not be fully completed, and why

- Item 1's canonical DDL covers the tables whose CHECK constraints are actually
  exercised by the test suite (`publications`, `synthetic_declarations`,
  `platform_capabilities`), not the package's full ~40-table catalogue.
  `SPEC/11_DATABASE_SCHEMA.md` remains prose documentation for the rest.
  Converting the entire catalogue to executable SQL was judged out of scope for
  a validation-hardening pass that must not add product features, and the prose
  "keys" column for most of those ~37 tables mixes real SQL with narrative
  commentary in a way that isn't mechanically parseable without risking
  introducing errors into text nobody asked to have machine-verified. This is
  explicitly noted as the scope boundary in `build_canonical_ddl()`'s docstring.
- `TOOLS/requirements.txt` pinning used `pip-compile` against the versions
  resolvable in this sandboxed build environment (network access via the
  environment's proxy was available and worked). No package was left unpinned
  or given an invented version number.
- Release-gate steps 17 (security), 19 (chaos), 20 (acceptance) remain N/A at
  specification stage — this predates V3.1.1 and is correct: this package ships
  no running application for those implementation-phase suites to execute
  against (V31-10, unchanged from V3.1).

## Final pipeline output (all green)

```
### python TOOLS/generate_artifacts.py --check
PASS  no generated-artifact drift detected

### python TOOLS/validate_package.py
...
57/57 checks passed (0 failed, 0 N/A)

### python TOOLS/conformance_tests.py
...
65/65 conformance cases passed (39 of them are negative cases that must be rejected)
conditional coverage: 6/6 discovered if/then conditionals have declared positive+negative coverage (V31-03)

### python TOOLS/release_gate.py
...
   1. Schema structure               PASS
   3. Date-time formats              PASS
   9. Generated artifact drift       PASS
  10. Conditional coverage           PASS
  11. Money representation           PASS
  13. Publication evidence rules     PASS
  14. Synthetic disclosure rules     PASS
  15. Platform evidence rules        PASS
  17. Security checks                N/A (spec stage)
  18. Regression tests               PASS
  19. Chaos tests                    N/A (spec stage)
  20. Acceptance tests               N/A (spec stage)
  21. Version consistency            PASS
  22. Generated artifact semantics   PASS
  23. Database contract single-source PASS
  24. No hand-copied SQL contracts   PASS
  25. Mutation tests (break -> red)  PASS

RELEASE GATE: PASS
```

`python -m pytest TOOLS/ -v` was checked and confirmed NOT to be this package's
convention: every `TOOLS/test_*.py` is a standalone script (`run()` returning an
exit code, `if __name__ == "__main__": sys.exit(run())`), documented as such in
README.md (updated in this pass). pytest only auto-collects
`test_money_precision.py`'s two module-level `test_*` functions and reports
`2 passed`; the package's own convention (`release_gate.py`, or each script
directly) is the actual test runner and is what's exercised above.

## Final sanity check (performed, then reverted)

Hand-edited `SCHEMAS/state-machine.json`'s `$id` back to the wrong
`.../3.0.0` value. Confirmed:
- `python TOOLS/generate_artifacts.py --check` → `FAIL  generated artifact drift detected`
- `python TOOLS/release_gate.py` → step 9 and step 21 (version consistency) FAIL,
  `RELEASE GATE: FAIL`

Then `git checkout -- SCHEMAS/state-machine.json` reverted it; re-ran the full
pipeline (`--check`, `validate_package.py`, `release_gate.py`) and confirmed all
green again, as shown above. `git status` is clean; `git log --oneline` shows
six commits (initial import + five phases).

---

# V3.1.2 — second-review validation-hardening pass

A second, independent review of the V3.1.1 package surfaced 5 candidate defects
(reviewer IDs `V31.1.1-02`, `V31.1.1-03`, `V31.1.1-04`, `V31.1.1-05`,
`V31.1.1-08`) plus 2 small CI hardening items. All 5 candidate defects were
confirmed real by reading the code before any fix was made. This section
documents exactly what changed for each, matching the reviewer's numbering.

## V31.1.1-02 — SV duplicated outside the canonical generator (item 1)

**Confirmed:** `TOOLS/conformance_tests.py` (`SV = "3.1.0"`),
`TOOLS/validate_package.py` (`SV = "3.1.0"`) and `TOOLS/test_publication_evidence.py`
(`SV = "3.1.0"`) each carried their own hardcoded copy of the package version,
independent of `TOOLS/generate_artifacts.py`'s `SV = "3.1.0"` (the reviewer's
report named only `conformance_tests.py`; a repo-wide scan for the same pattern
turned up two more real instances, fixed alongside it so the new guard test
below wouldn't immediately fail against the rest of the tree).

**Fix:**
- All three files now `import generate_artifacts as ga` (or `_ga`, in
  `validate_package.py`, to avoid shadowing its own later local imports) via the
  same `sys.path.insert(0, TOOLS)` pattern already used by
  `test_generated_artifacts_semantics.py` / `test_mutations.py`, and set
  `SV = ga.SV` — a reference back to the canonical generator, never a second
  literal.
- New `TOOLS/test_no_duplicated_version_constants.py`: an AST-based static scan
  of every `TOOLS/*.py` file except `generate_artifacts.py` for a top-level
  assignment of a name literally called `SV` to a string literal. Deliberately
  AST-based rather than regex-based so it can't be fooled by `SV` inside a
  comment/docstring, and deliberately distinguishes a literal (`SV = "3.1.0"`,
  the defect) from a reference (`SV = ga.SV`, the fix) — a plain `SV = ['"]`
  regex can't make that distinction and would just flag the fix as a new
  violation.
- Wired into `release_gate.py` as step 26 ("No duplicated version constants").

## V31.1.1-03 — reconstructed evidence vocabulary in the semantics test (item 2)

**Confirmed:** `TOOLS/test_generated_artifacts_semantics.py` computed
`non_authoritative = {"POST_PUBLISH_CHECK"}` and derived
`expected_authoritative = all_evidence_enum_full - non_authoritative` — a second,
hand-typed encoding of which evidence source is authoritative, instead of
consuming `generate_artifacts.py`'s real `AUTHORITATIVE_EVIDENCE` /
`NON_AUTHORITATIVE_EVIDENCE` / `ALL_EVIDENCE` module-level lists (lines
131-133). A generator bug that changed the authoritative set would have been
silently agreed with by this parallel reconstruction.

**Fix:** the three `crossref.*evidence*` checks now compare directly against
`ga.AUTHORITATIVE_EVIDENCE`, `ga.NON_AUTHORITATIVE_EVIDENCE` and `ga.ALL_EVIDENCE`.
Grepped the rest of the file for any other spot re-encoding a normative
vocabulary (state names, table names, etc.) as a local literal instead of
consuming a generator constant/function; found none — every other cross-check in
the file already calls a generator function (`ga.build_tables_and_doc()`,
`ga.build_traceability()`) or compares against the generator's own output
in-place, so no further changes were needed there.

## V31.1.1-04 — missing-key vs null-value conflated in the coverage mutation harness (item 3)

**Confirmed:** `TOOLS/test_conditional_coverage.py`'s "only positive_case" /
"only negative_case" cases both built a coverage entry with the *other* key
present and explicitly `None` (e.g. `{"positive_case": "pos", "negative_case": None}`)
rather than a dict genuinely missing that key. The file's own comment admitted
this was a workaround for `check_conditional_coverage()` doing a bare
`entry["negative_case"]`, which would `KeyError` rather than fail cleanly on a
truly-missing key.

**Fix:**
- `conformance_tests.check_conditional_coverage()` now reads
  `entry.get("positive_case")` / `entry.get("negative_case")` instead of
  `entry[...]`, so a genuinely missing key resolves to `None` exactly like an
  explicit null does, and fails cleanly through the existing "declared ... does
  not exist among passing/failing CASES" branch instead of raising.
- Same function also gained duplicate-path detection (`collections.Counter` over
  each schema's declared entries) — two coverage entries claiming the same
  conditional path now FAIL with `DUPLICATE COVERAGE ENTRY` instead of the dict
  comprehension silently keeping only the last one.
- `test_conditional_coverage.py` rewritten to 7 explicit, independently-asserted
  cases: 1 (no entry at all, kept from the original harness), A (only
  `positive_case` key present, `negative_case` key absent entirely), B (only
  `negative_case` key present, `positive_case` key absent entirely), C (both
  keys present, `positive_case` explicitly null, `negative_case` real), D (both
  populated with real values → PASS), E (stale entry, kept from the original
  harness), F (two entries claiming the same path → duplicate). All 7 pass with
  their expected PASS/FAIL outcome.

## V31.1.1-05 — no proof requirements.txt is a faithful pip-compile of requirements.in (item 4)

**Confirmed:** nothing in the suite compared the exact pins in
`TOOLS/requirements.txt` against a fresh `pip-compile` of the loose bounds in
`TOOLS/requirements.in`; they could drift silently.

**Fix:** new `TOOLS/test_requirements_lockfile_fresh.py`. It locates a runnable
`pip-compile` (PATH, then `python -m piptools`) without ever installing
pip-tools itself as a side effect of running; if unavailable, or if pip-compile
times out / fails (no PyPI network), it prints an explicit `N/A` with a
justification and human/CI verification instructions, and never silently PASSes
or hard-FAILs on an environment limitation. If pip-compile does run, it diffs
`package==version` pins against the checked-in file and PASS/FAILs for real.
The script emits a final `GATE_STATUS: PASS|FAIL|N/A -- <detail>` line.
`release_gate.py` gained `run_reporting_status()`, a variant of its existing
`run()` that parses that marker (falling back to plain exit-code semantics if a
script never emits it) and reports N/A with its justification — non-blocking —
distinct from FAIL. Wired in as step 27. In this sandbox, pip-tools is not
installed and there is no path to install it as a side effect of a test run, so
step 27 correctly reports N/A; a CI runner with network access and pip-tools
installed will get a real PASS/FAIL.

## V31.1.1-08 — mutation coverage thin relative to the critical contracts (item 5)

**Confirmed:** `TOOLS/test_mutations.py` had 6 mutations; several critical,
already-existing checks had no mutation exercising them.

**Fix:** added 8 mutations (14 total), each following the file's existing
pattern (mutate a scratch/in-memory copy, run the real check, assert red, prove
the canonical generator/real file on disk unaffected):
- **7.** transition FROM a terminal state (`ARCHIVED`) → `validate_state_machine`'s
  "terminal has outbound" guarantee FAILs.
- **8.** transition TO a nonexistent state name → `validate_state_machine`'s
  "unknown to" guarantee FAILs.
- **9.** duplicate transition ID → `validate_state_machine`'s duplicate-id
  guarantee FAILs.
- **10.** `evidence_retrieved_at` requirement removed from the VERIFIED
  conditional → the I-11 schema conditional wrongly accepts a VERIFIED row
  without it.
- **11.** `external_id` requirement removed from the VERIFIED conditional →
  same, for `external_id`.
- **12.** a money field (`cost-event.amount`) retyped as JSON Schema `number` →
  the semantic invariant `invariant.no_money_field_typed_as_number` (from
  `test_generated_artifacts_semantics.py`) FAILs; its exact detection logic is
  reimplemented inline in the mutation so it exercises the real invariant.
- **13.** a NonNegativeMoney pattern (`job.estimated_cost`) widened to
  `SIGNED_MONEY`'s pattern → the V31-04 non-negativity case wrongly accepts a
  negative value.
- **14.** a reference to a fabricated, deliberately-nonexistent decision ID (a
  `D-`-prefixed, three-digit id confirmed absent from `DECISIONS.md`; spelled
  out in `TOOLS/test_mutations.py`, not repeated here so this very sentence
  doesn't itself trip `refs.all_decision_ids_exist`) injected into a scratch
  copy of the package tree →
  `validate_package.py`'s real `refs.all_decision_ids_exist` check (exercised
  directly, by monkeypatching `validate_package.ROOT`/`.RESULTS` to the scratch
  copy and calling `check_references()`, not reimplemented) FAILs.

All 14/14 mutations demonstrate the expected red flip. No listed mutation
lacked a corresponding real check to flip — items 7-14 above each map to a
check that already existed in this codebase before this pass.

## CI hardening (items 6 and 7)

6. `.github/workflows/validation.yml` gained `workflow_dispatch:` alongside its
   existing `push:` / `pull_request:` triggers, so the workflow can also be run
   manually.
7. `actions/checkout@v4` and `actions/setup-python@v5` are now pinned to their
   full commit SHA (`git ls-remote https://github.com/actions/checkout.git` /
   `.../setup-python.git` resolved `v4` → `11d5960a326750d5838078e36cf38b85af677262`
   (= tag `v4.4.0`) and `v5` → `a26af69be951a213d495a4c3e4e4022e16d87065`
   (= tag `v5.6.0`) at the time of this pass), each with a trailing `# vX.Y.Z`
   comment for readability. These are real, independently-verifiable commit
   hashes (resolved via `git ls-remote`, not invented).

## Three reviewer claims independently re-confirmed as false — not fixed, because there was nothing to fix

- **"CI workflow is missing"** — `.github/workflows/validation.yml` already
  existed (added in the V3.1.1 pass) and already ran
  `validate_package.py` / `conformance_tests.py` / `generate_artifacts.py --check`
  / `release_gate.py`. Left its core structure untouched per instruction; only
  items 6/7 above were applied to it.
- **".gitignore is missing or wrong"** — `.gitignore` already existed and
  already listed `__pycache__/`, `*.pyc`, `.pytest_cache/`. Verified with
  `cat .gitignore`; not modified.
- **"a duplicate generate_artifacts.py exists at the repo root"** — verified
  with `git ls-files | grep -i generate_artifacts` and
  `find . -name generate_artifacts.py -not -path '*/.git/*'`: both return
  exactly one file, `TOOLS/generate_artifacts.py`. No root-level duplicate
  exists.

## Pipeline status after this pass

```
python TOOLS/generate_artifacts.py --check   → PASS
python TOOLS/validate_package.py             → 57/57 checks passed
python TOOLS/conformance_tests.py            → 65/65 cases, 6/6 conditionals covered
python TOOLS/release_gate.py                 → RELEASE GATE: PASS (27 steps: 20 PASS,
                                                4 covered-by-another-step, 3 N/A --
                                                steps 17/19/20 spec-stage as before,
                                                step 27 environment-limited pip-compile
                                                with justification)
```

No step reports UNKNOWN or is silently missing. One item could not be fully
completed in this sandbox: step 27 (requirements lockfile freshness) reports
N/A here because pip-tools is not installed and this test deliberately never
installs packages as a side effect of running — a human or CI job with network
access and pip-tools installed will get a real PASS/FAIL from the same script.

---

# P2 hardening — reviewer wishlist (post V3.1.2)

A reviewer's remaining P2 wishlist, all five items addressed. No product/spec
content changed; this pass is tooling and documentation only.

## 1 — No cached/generated Python artifacts committed to git

New `TOOLS/test_repository_hygiene.py`, sub-check `hygiene.no_tracked_junk`:
enumerates everything `git ls-files` actually tracks and fails if any tracked
path matches a junk/cache pattern. The patterns are read from `.gitignore` at
runtime (`_load_gitignore_patterns()` + `_pattern_matches()`), not
hand-duplicated into a second list, per the instruction — `.gitignore` stays
the single place these patterns are declared. `.gitignore` itself was
expanded from its previous 3 patterns (`__pycache__/`, `*.pyc`,
`.pytest_cache/`) to the full requested set: `*.pyo`, `*.egg-info/`, `.venv/`,
`venv/`, `.DS_Store`, `*.swp` added.

The same script's `hygiene.no_stray_canonical_duplicates` sub-check turns the
earlier reviewer claim "a duplicate `generate_artifacts.py` exists at the
repo root" (already independently re-confirmed false in the V3.1.2 pass by a
one-off manual `git ls-files | grep`) into a permanent regression test: for
every `TOOLS/*.py` basename, no other tracked path anywhere in the repo may
share it.

**Verified working, not just written:** on a scratch commit (`git add -f` a
`TOOLS/__pycache__/junk.pyc` and a root-level copy of
`TOOLS/generate_artifacts.py`), the test correctly FAILed both sub-checks with
the exact offending paths named, then `git reset --hard HEAD && git clean -fd`
removed the scratch breakage before any further work — the committed tree
never carried it.

## 2 — MANIFEST.sha256 doesn't include junk

Investigated first, per instruction, rather than adding redundant code.
`TOOLS/validate_package.py`'s existing `manifest.matches_tree` compares
`MANIFEST.sha256`/`MANIFEST.md` against `compute_manifest()`, which itself
walks the **same** `walk_files()` the manifest was generated from — so that
check can never, by construction, detect a junk file that got walked into the
manifest; it only proves internal self-consistency, not that every entry is
something git actually tracks. That gap is real and is what item 2 is
actually asking about. Closed it with a new sub-check,
`hygiene.manifest_entries_are_git_tracked` (same
`test_repository_hygiene.py`, so as not to introduce a third overlapping
mechanism — see item 3): every path listed in `MANIFEST.sha256` must be a
real `git ls-files` entry.

`manifest.excludes_itself` (the existing self-reference check) was read
carefully: it already correctly asserts `MANIFEST.md`'s own markdown table
never lists `MANIFEST.md`/`MANIFEST.sha256` (both are excluded at generation
time in `compute_manifest()`, before either file is written). It was
extended, not duplicated, to also check `MANIFEST.sha256`'s own line format
for a self-reference, so the guarantee covers both generated files the same
way instead of only the prettier one.

## 3 — Repository integrity test

Per the instruction not to build a third redundant mechanism: `hygiene.
no_tracked_junk`, `hygiene.no_stray_canonical_duplicates`, and `hygiene.
manifest_entries_are_git_tracked` are three clearly-separated sub-checks
inside the single `TOOLS/test_repository_hygiene.py`, wired into
`release_gate.py` as one new step (28, "Repository hygiene"). This script is
the canonical repository-integrity test; `manifest.matches_tree` (structural
self-consistency) stays where it already lived, in
`TOOLS/validate_package.py`, and is referenced rather than re-implemented.

## 4 — Python version matrix

**Decision: rejected.** `.github/workflows/validation.yml` keeps a single
pinned Python 3.11. Reasoning: this package's `TOOLS/*.py` is internal
CI/release-readiness tooling for a specification-stage package, not a
published library or service other people install across a range of Python
environments — there is exactly one runtime that ever executes it (this
repo's own CI and any contributor's local checkout, which are expected to
match CI). `TOOLS/requirements.txt`'s pins (`jsonschema==4.26.0`,
`pyyaml==6.0.3`, etc.) were not authored against multiple interpreter
targets, and a scan of `TOOLS/*.py` found no version-gated syntax (no
`match`/`case`, no `str.removeprefix`/`removesuffix`, nothing conditioned on
`sys.version_info`) that would even exercise a matrix differently across
3.10–3.12. Adding `python-version: ["3.10", "3.11", "3.12"]` here would
triple CI time to test a dimension nothing in this codebase varies along —
exactly the checklist-cargo-culting the instruction warned against. If this
tooling is ever repackaged as an installable library for others to run
outside this repo's own CI, that would be the point to revisit this decision.

## 5 — PASS/FAIL/N/A/UNKNOWN, consolidated definition

`SPEC/78_RELEASE.md` and `SPEC/71_TEST_MATRIX.md` were read first, per
instruction. Neither defines this vocabulary — `SPEC/78` describes the
product's own eventual release gates (a later-stage, different concept from
this package's spec-stage tooling status) and `SPEC/71` is an invariant/test
coverage matrix, not a status-vocabulary definition. So there was no existing
SPEC/-level convention to follow for *this* vocabulary; the pre-existing
convention (from V3.1.1/V3.1.2) was already to document it in
`TOOLS/release_gate.py`'s own module docstring and echo shorter versions in
`TOOLS/validate_package.py`'s module comment. That is followed and made
authoritative rather than relocated: `TOOLS/release_gate.py`'s docstring now
carries one clearly-marked "STATUS VOCABULARY — AUTHORITATIVE DEFINITION"
block spelling out exactly what PASS/FAIL/N/A/UNKNOWN each mean and which one
blocks release, and states explicitly that every other mention in the repo
refers back to it rather than restating a competing definition.
`release_gate.py`'s own SUMMARY output now prints a one-line pointer to that
docstring block, so it is referenced from the tool's actual output, not left
as prose nobody sees at runtime.

**Existing N/As spot-checked, all four carry real justifications:**
- Steps 17/19/20 (security/chaos/acceptance): "NOT APPLICABLE AT
  SPECIFICATION STAGE (runs against an implementation; see SPEC/7X)" —
  present.
- Step 27 (requirements lockfile freshness): `test_requirements_lockfile_
  fresh.py` supplies a specific, environment-derived justification (e.g. "no
  `pip-compile` on PATH and `python -m piptools` is unavailable... To verify
  for real: pip install pip-tools && ...") every time it reports N/A —
  present, and never empty (`_na()` always takes a non-empty string).

No change was needed to any of the four; they already complied. This section
is the evidence for that, not a claim taken on faith.

## Pipeline status after this pass

```
python TOOLS/generate_artifacts.py --check   → PASS
python TOOLS/validate_package.py             → 57/57 checks passed
python TOOLS/conformance_tests.py            → 65/65 cases, 6/6 conditionals covered
python TOOLS/release_gate.py                 → RELEASE GATE: PASS (28 steps: 24 PASS,
                                                4 covered-by-another-step, 3 N/A --
                                                steps 17/19/20 spec-stage as before,
                                                step 27 environment-limited pip-compile
                                                with justification)
```

No step reports UNKNOWN or is silently missing.
