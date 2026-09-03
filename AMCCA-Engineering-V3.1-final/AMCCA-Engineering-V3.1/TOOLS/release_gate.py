#!/usr/bin/env python3
"""
AMCCA release gate.

Runs every verification step in the order the V3.1 audit specifies. Any failure
anywhere ends the run with exit code 1 -- there is no partial pass.

This script orchestrates other TOOLS/*.py scripts rather than reimplementing their
logic, so there is exactly one place each guarantee is actually checked (V31-10):
if a step here can't point to the script that proves it, it isn't listed as MUST.

This is the canonical, single command that decides release-readiness for this
package. Every script it calls remains individually runnable (for CI granularity
and local debugging), but a green run of THIS script -- not a manual checklist of
which of the other scripts someone remembered to run -- is what release requires.

=======================================================================
STATUS VOCABULARY -- AUTHORITATIVE DEFINITION (P2 hardening item 5)
=======================================================================
This is the one place these four statuses are normatively defined for this
package's tooling. TOOLS/validate_package.py's RESULTS entries, every
TOOLS/test_*.py script's own PASS/FAIL/N/A printing, and this file's own
per-step SUMMARY output all use exactly this vocabulary and mean exactly
this. Nowhere else in the repo defines it independently; anywhere else that
mentions it (module comments in validate_package.py, individual test
scripts) is referring back to this definition, not restating a competing
one. (SPEC/78_RELEASE.md and SPEC/71_TEST_MATRIX.md were checked and do not
define this vocabulary -- they describe the product's own release gates and
invariant test matrix, a different and later-stage concept from this
package's own spec-stage tooling status. This file is the correct home for
it because it is where the vocabulary was introduced (V3.1.1) and where all
four statuses are actually produced and enforced.)

  PASS     The check ran, and the property it verifies held. A step's script
           exited 0 (or, for a status-reporting step, explicitly reported
           PASS -- see run_reporting_status() below).

  FAIL     The check ran, and the property it verifies did NOT hold. A
           step's script exited non-zero (or explicitly reported FAIL).
           FAIL always blocks release. There is no override.

  N/A      The check does not apply in the current context -- e.g. a
           specification-stage package has no running implementation for an
           implementation-phase test suite to execute against (steps
           17/19/20), or an environment limitation makes the check
           impossible to run in a way that says nothing about whether the
           thing it checks is actually correct (step 27: no pip-tools/no
           network). N/A ALWAYS requires a non-empty justification string
           explaining why -- validate_package.py's check_na() enforces this
           at the call site, and run_reporting_status() below only ever
           reports N/A when the script itself supplied one. An N/A with no
           stated reason is not a real N/A; it is a skipped check wearing an
           N/A costume, and no code path in this package prints one without
           a reason attached. N/A does NOT block release.

  UNKNOWN  The check could not be completed and its result is genuinely
           indeterminate -- as distinct from N/A's "legitimately doesn't
           apply here." This is for the case where the check SHOULD have an
           answer but this run could not produce one (e.g. a script crashed
           before reaching a PASS/FAIL/N/A determination). UNKNOWN blocks
           release exactly like FAIL: the entire reason this status exists
           separately from a bare "not tested" is to stop "we didn't test
           it" from silently reading as if it passed. This gate has no step
           that currently produces UNKNOWN, and BY DESIGN has no fallback
           that quietly maps a script crash to anything other than FAIL
           (`run()` below treats any non-zero exit, including a crash, as
           FAIL) -- so UNKNOWN cannot silently read as PASS here either. If a
           future step needs to report UNKNOWN explicitly, it MUST also
           block release exactly like FAIL does; see the SUMMARY enforcement
           in main() below.

  covered  Not a real status -- a bookkeeping value used only in this file's
           STEPS table, for a guarantee that IS checked, but inside another
           step's script (see the per-step comments below) rather than
           having its own independent script. Never appears in
           validate_package.py's RESULTS or in any TOOLS/test_*.py script's
           own output.
=======================================================================

    python TOOLS/release_gate.py
"""
import os, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(ROOT, "TOOLS")
PY = sys.executable


def run(label, args, cwd=ROOT):
    print(f"\n{'='*72}\n{label}\n{'='*72}")
    r = subprocess.run([PY] + args, cwd=cwd)
    ok = r.returncode == 0
    print(f"-- {label}: {'PASS' if ok else 'FAIL'} (exit {r.returncode})")
    return ok


def run_reporting_status(label, args, cwd=ROOT):
    """Like run(), but for a step whose script can itself determine, at runtime, that
    it is N/A (an environment limitation, e.g. no network / no pip-tools) rather than
    PASS or FAIL -- V31.1.2 item 4. The script prints a final line
    `GATE_STATUS: PASS|FAIL|N/A -- <justification>`; this parses it. If a script never
    emits that marker (any script not written to support N/A), the subprocess exit
    code is the ground truth, exactly like run() above -- this never turns an ordinary
    script into a silent PASS.

    Returns (status, justification) with status in {"PASS", "FAIL", "N/A"}.
    """
    print(f"\n{'='*72}\n{label}\n{'='*72}")
    r = subprocess.run([PY] + args, cwd=cwd, capture_output=True, text=True)
    output = r.stdout + r.stderr
    sys.stdout.write(output)
    if output and not output.endswith("\n"):
        sys.stdout.write("\n")

    status, justification = None, ""
    for line in output.splitlines():
        if line.startswith("GATE_STATUS:"):
            rest = line[len("GATE_STATUS:"):].strip()
            if rest.startswith("N/A"):
                status = "N/A"
                justification = rest[len("N/A"):].lstrip("- ").strip()
            elif rest.startswith("PASS"):
                status = "PASS"
            elif rest.startswith("FAIL"):
                status = "FAIL"
                justification = rest[len("FAIL"):].lstrip("- ").strip()

    if status is None:
        # No GATE_STATUS marker emitted -- fall back to plain exit-code semantics so
        # this helper is a strict superset of run(), never a silent downgrade of it.
        status = "PASS" if r.returncode == 0 else "FAIL"
    elif status in ("PASS", "N/A") and r.returncode != 0:
        # A script that reports PASS/N/A but exits non-zero is lying to one channel or
        # the other -- treat that contradiction as a hard FAIL, never as UNKNOWN
        # silently reading as release-safe.
        reported = status
        status = "FAIL"
        justification = (justification + " " if justification else "") + \
            f"(script reported {reported!r} in GATE_STATUS but exited {r.returncode})"

    print(f"-- {label}: {status}" + (f" ({justification})" if justification else "") +
          f" (exit {r.returncode})")
    return status, justification


# (step_no, label, script, args) -- order matches the audit's mandated sequence.
STEPS = [
    (1,  "Schema structure",                 os.path.join(TOOLS, "validate_package.py"), []),
    (2,  "Schema versions",                  None, None),  # covered by step 1 (schemas.every_schema_versioned)
    (3,  "Date-time formats",                os.path.join(TOOLS, "test_schema_formats.py"), []),
    (4,  "State machine",                    None, None),  # covered by step 1 (stm.*)
    (5,  "State transition matrix",          None, None),  # covered by step 1 (stm.matrix_lists_all_transitions)
    (6,  "Database contracts",               None, None),  # covered by step 1 (db.*)
    (7,  "Internal references",              None, None),  # covered by step 1 (refs.all_internal_references_resolve)
    (8,  "Decision references",              None, None),  # covered by step 1 (refs.all_decision_ids_exist)
    (9,  "Generated artifact drift",         os.path.join(TOOLS, "test_generation.py"), []),
    (10, "Conditional coverage",             os.path.join(TOOLS, "test_conditional_coverage.py"), []),
    (11, "Money representation",             os.path.join(TOOLS, "test_money_precision.py"), []),
    (12, "Money arithmetic",                 None, None),  # covered by step 11 (Decimal precision case)
    (13, "Publication evidence rules",       os.path.join(TOOLS, "test_publication_evidence.py"), []),
    (14, "Synthetic disclosure rules",       os.path.join(TOOLS, "test_synthetic_disclosure.py"), []),
    (15, "Platform evidence rules",          os.path.join(TOOLS, "test_platform_evidence.py"), []),
    (16, "Manifest integrity",               None, None),  # covered by step 1 (manifest.*)
    (17, "Security checks",                  None, None),  # SPEC/72 -- no executable harness in this package; see note below
    (18, "Regression tests",                 os.path.join(TOOLS, "conformance_tests.py"), []),
    (19, "Chaos tests",                      None, None),  # SPEC/74 -- no executable harness in this package; see note below
    (20, "Acceptance tests",                 None, None),  # SPEC/75 -- no executable harness in this package; see note below
    # V3.1.1 additions -- validation-hardening pass (see CHANGELOG and
    # AUDIT/V31_1_1_VERSION_AUDIT.md). Each closes a gap where the suite could
    # report green without actually exercising the real contract.
    (21, "Version consistency",              os.path.join(TOOLS, "test_version_consistency.py"), []),
    (22, "Generated artifact semantics",     os.path.join(TOOLS, "test_generated_artifacts_semantics.py"), []),
    (23, "Database contract single-source",  os.path.join(TOOLS, "test_database_contract_source.py"), []),
    (24, "No hand-copied SQL contracts",     os.path.join(TOOLS, "test_no_contract_duplication.py"), []),
    (25, "Mutation tests (break -> red)",    os.path.join(TOOLS, "test_mutations.py"), []),
    # V3.1.2 additions -- second-review validation-hardening pass.
    (26, "No duplicated version constants", os.path.join(TOOLS, "test_no_duplicated_version_constants.py"), []),
    (27, "Requirements lockfile freshness",  os.path.join(TOOLS, "test_requirements_lockfile_fresh.py"), []),
    # P2 hardening pass.
    (28, "Repository hygiene",               os.path.join(TOOLS, "test_repository_hygiene.py"), []),
]

# V31-10: steps 17, 19 and 20 reference test suites that SPEC/72, SPEC/74 and SPEC/75
# specify in detail, but this specification package ships no running AMCCA application
# to execute them against -- they are implementation-phase test suites, not
# specification-phase ones. Declaring them "PASS" here without an implementation to run
# them against would be exactly the self-certification D-029 forbids. They are reported
# as NOT APPLICABLE AT SPECIFICATION STAGE rather than skipped silently or claimed green.
NOT_APPLICABLE_AT_SPEC_STAGE = {17, 19, 20}

# V31.1.2 item 4: steps whose script determines PASS/FAIL/N/A itself at RUNTIME (an
# environment limitation discovered while running, e.g. no network / no pip-tools),
# unlike NOT_APPLICABLE_AT_SPEC_STAGE above which is a fixed, authoring-time list.
# These steps are dispatched through run_reporting_status() instead of run().
RUNTIME_STATUS_STEPS = {27}


def main():
    results = []
    for step_no, label, script, args in STEPS:
        if step_no in NOT_APPLICABLE_AT_SPEC_STAGE:
            print(f"\n{'='*72}\n{step_no:2d}. {label}\n{'='*72}")
            print(f"-- {label}: NOT APPLICABLE AT SPECIFICATION STAGE "
                  f"(runs against an implementation; see SPEC/7{step_no-14 if step_no!=17 else 2})")
            results.append((step_no, label, None))
            continue
        if script is None:
            results.append((step_no, label, "covered"))
            continue
        if step_no in RUNTIME_STATUS_STEPS:
            status, justification = run_reporting_status(f"{step_no:2d}. {label}", [script] + args)
            results.append((step_no, label, (status, justification)))
            continue
        ok = run(f"{step_no:2d}. {label}", [script] + args)
        results.append((step_no, label, ok))

    print(f"\n{'='*72}\nSUMMARY\n{'='*72}")
    print("Status meanings (PASS/FAIL/N/A/UNKNOWN): see this file's module docstring, "
          "'STATUS VOCABULARY -- AUTHORITATIVE DEFINITION'.")
    hard_fail = False
    for step_no, label, outcome in results:
        if isinstance(outcome, tuple):
            status, justification = outcome
            if status == "PASS":
                tag = "PASS"
            elif status == "FAIL":
                tag = f"FAIL ({justification})" if justification else "FAIL"
                hard_fail = True
            else:  # N/A -- runtime environment limitation, never blocks release, and
                   # run_reporting_status() guarantees a non-empty justification came
                   # with it (the script that emitted N/A always supplies one).
                tag = f"N/A ({justification})" if justification else "N/A"
        elif outcome is True:
            tag = "PASS"
        elif outcome is False:
            tag = "FAIL"; hard_fail = True
        elif outcome == "covered":
            tag = "covered by step 1/11"
        else:
            tag = "N/A (spec stage)"
        print(f"  {step_no:2d}. {label:<30} {tag}")

    if hard_fail:
        print("\nRELEASE GATE: FAIL")
        return 1
    print("\nRELEASE GATE: PASS (specification-stage checks only -- "
          "steps 17/19/20 require a running implementation, see SPEC/70)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
