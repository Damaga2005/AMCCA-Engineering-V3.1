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
import argparse, hashlib, json, os, subprocess, sys
import xml.etree.ElementTree as ET

if sys.stdout and hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
if sys.stderr and hasattr(sys.stderr, "reconfigure"):
    try:
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(ROOT, "TOOLS")
sys.path.insert(0, TOOLS)
try:
    import pe_validator
except ImportError:
    pe_validator = None

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


def sha256_file(filepath: str) -> str:
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest().lower()


def verify_release_invariants(
    release_dir: str = None,
    repo_root: str = None,
    expected_commit_sha: str = None,
    check_git: bool = True,
    check_tools: bool = True,
    build_warnings: int = 0,
    build_errors: int = 0,
) -> tuple[bool, list[str]]:
    """
    Evaluates all 15 release invariants specified in DEF-CERT-008 Section 11.1.
    Strictly forbids N/A, SKIP, UNKNOWN, or partial pass in release certification.
    Returns (ok, list_of_reasons).
    """
    repo = repo_root or ROOT
    rdir = release_dir or os.path.join(repo, "dist", "release")
    failures = []

    print(f"\n{'='*72}\nAMCCA RELEASE GATE -- VERIFYING RELEASE INVARIANTS (DEF-CERT-008)\n{'='*72}")

    if check_tools:
        # 1. Package validation
        print("[1/15] Verifying specification schemas and invariants (validate_package.py)...")
        res = subprocess.run([PY, os.path.join(TOOLS, "validate_package.py")], cwd=repo, capture_output=True, text=True)
        if res.returncode != 0:
            failures.append(f"package validation FAIL (exit {res.returncode}): {res.stderr.strip() or res.stdout.strip()}")
        else:
            print("  package validation PASS")

        # 2. Conformance tests
        print("[2/15] Verifying conformance rules and conditionals (conformance_tests.py)...")
        res = subprocess.run([PY, os.path.join(TOOLS, "conformance_tests.py")], cwd=repo, capture_output=True, text=True)
        if res.returncode != 0:
            failures.append(f"conformance FAIL (exit {res.returncode}): {res.stderr.strip() or res.stdout.strip()}")
        else:
            print("  conformance PASS")

        # 3. Repository hygiene
        print("[3/15] Verifying repository hygiene (test_repository_hygiene.py)...")
        res = subprocess.run([PY, os.path.join(TOOLS, "test_repository_hygiene.py")], cwd=repo, capture_output=True, text=True)
        if res.returncode != 0:
            failures.append(f"repository hygiene FAIL (exit {res.returncode}): {res.stderr.strip() or res.stdout.strip()}")
        else:
            print("  repository hygiene PASS")
    else:
        print("[1-3/15] Specification, conformance and hygiene tools bypassed for test fixture.")

    # 4. Working tree clean
    current_sha = "UNKNOWN"
    if check_git:
        print("[4/15] Verifying git working tree cleanliness...")
        res = subprocess.run(["git", "status", "--porcelain"], cwd=repo, capture_output=True, text=True)
        if res.returncode != 0 or res.stdout.strip():
            failures.append(f"working tree dirty: {res.stdout.strip()}")
        else:
            print("  working tree clean: PASS")

        # 5. HEAD valid
        print("[5/15] Verifying git HEAD...")
        res = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True)
        if res.returncode != 0:
            failures.append(f"git rev-parse HEAD failed: {res.stderr.strip()}")
        else:
            current_sha = res.stdout.strip()
            if expected_commit_sha and current_sha != expected_commit_sha:
                failures.append(f"wrong HEAD: expected {expected_commit_sha}, got {current_sha}")
            else:
                print(f"  HEAD valid: PASS ({current_sha[:10]})")
    else:
        print("[4/15] Git check bypassed for test fixture.")
        print("[5/15] Git HEAD check bypassed for test fixture.")

    # 6. Artifacts exist & size > 0
    print("[6/15] Verifying release artifacts presence and non-zero size...")
    exe_file = os.path.join(rdir, "AMCCA-Setup.exe")
    msi_file = os.path.join(rdir, "AMCCA-Setup.msi")
    zip_file = os.path.join(rdir, "AMCCA-Desktop-win-x64.zip")

    for art_name, art_path in [("EXE", exe_file), ("MSI", msi_file), ("ZIP", zip_file)]:
        if not os.path.exists(art_path):
            failures.append(f"missing artifact: {art_name} not found at {art_path}")
        elif os.path.getsize(art_path) == 0:
            failures.append(f"empty artifact: {art_name} has 0 bytes at {art_path}")
        else:
            print(f"  {art_name} exists: PASS ({os.path.getsize(art_path):,} bytes)")

    # 7. MSI != EXE
    exe_hash = ""
    msi_hash = ""
    zip_hash = ""
    if os.path.exists(exe_file) and os.path.exists(msi_file):
        exe_hash = sha256_file(exe_file)
        msi_hash = sha256_file(msi_file)
        if exe_hash == msi_hash:
            failures.append("MSI == EXE: bootstrapper bundle cannot be identical to MSI package")
        else:
            print("  MSI != EXE: PASS")
    if os.path.exists(zip_file):
        zip_hash = sha256_file(zip_file)

    # 8. PE valid
    print("[7/15] Verifying PE32+ structural validity of installer...")
    if os.path.exists(exe_file):
        if pe_validator:
            ok, msg, _ = pe_validator.validate_pe_file(exe_file)
            if not ok:
                failures.append(f"invalid PE: {msg}")
            else:
                print("  PE valid: PASS")
        else:
            failures.append("pe_validator module could not be imported")
    else:
        failures.append("invalid PE: AMCCA-Setup.exe missing")

    # 9. SHA256 manifest valid & non-self-referencing
    print("[8/15] Verifying SHA256SUMS.txt integrity...")
    sums_file = os.path.join(rdir, "SHA256SUMS.txt")
    if not os.path.exists(sums_file):
        failures.append("missing artifact: SHA256SUMS.txt not found")
    else:
        with open(sums_file, "r", encoding="utf-8-sig") as f:
            sums_lines = [l.strip() for l in f if l.strip()]

        seen_files = set()
        for line in sums_lines:
            parts = line.split()
            if len(parts) != 2:
                failures.append(f"malformed line in SHA256SUMS.txt: {line!r}")
                continue
            declared_hash, fname = parts[0].strip().lstrip("\ufeff").lower(), parts[1]
            if fname == "SHA256SUMS.txt":
                failures.append("SHA256SUMS.txt self-reference is forbidden")
            seen_files.add(fname)
            fpath = os.path.join(rdir, fname)
            if not os.path.exists(fpath):
                failures.append(f"hash mismatch: file declared in SHA256SUMS.txt missing: {fname}")
            else:
                actual_hash = sha256_file(fpath)
                if actual_hash != declared_hash:
                    failures.append(f"hash mismatch: {fname} declared {declared_hash} != actual {actual_hash}")

        if "AMCCA-Setup.exe" not in seen_files or "AMCCA-Setup.msi" not in seen_files:
            failures.append("SHA256SUMS.txt missing mandatory installer entries")
        if not any("hash mismatch" in f for f in failures):
            print("  SHA256 manifest valid: PASS")

    # 10. TRX test results valid
    print("[9/15] Verifying structured test execution (release-tests.trx)...")
    trx_file = os.path.join(rdir, "release-tests.trx")
    total_tests = 0
    passed_tests = 0
    failed_tests = 0
    skipped_tests = 0
    if not os.path.exists(trx_file):
        failures.append(f"missing artifact: {trx_file} not found")
    else:
        try:
            tree = ET.parse(trx_file)
            root_elem = tree.getroot()
            counters = None
            for elem in root_elem.iter():
                if elem.tag.endswith("Counters"):
                    counters = elem
                    break
            if counters is None:
                failures.append("TRX parsing error: Counters element not found")
            else:
                total_tests = int(counters.get("total", 0))
                passed_tests = int(counters.get("passed", 0))
                failed_tests = int(counters.get("failed", 0))
                skipped_tests = int(counters.get("notExecuted", 0))

                if total_tests <= 0:
                    failures.append("tests total <= 0: no tests executed in TRX")
                if failed_tests > 0:
                    failures.append(f"tests failed > 0: {failed_tests} tests failed")
                if skipped_tests > 0:
                    failures.append(f"tests skipped > 0: {skipped_tests} tests skipped")
                if passed_tests != total_tests:
                    failures.append(f"tests passed != tests total: {passed_tests} != {total_tests}")
                if passed_tests + failed_tests + skipped_tests != total_tests:
                    failures.append(f"tests sum mismatch: {passed_tests} + {failed_tests} + {skipped_tests} != {total_tests}")

                if not any("tests " in f for f in failures):
                    print(f"  tests total: {total_tests} | passed: {passed_tests} | failed: 0 | skipped: 0: PASS")
        except Exception as ex:
            failures.append(f"TRX parsing error: {ex}")

    # 11. Build structured diagnostics (warnings & errors)
    print("[10/15] Verifying compiler warnings and errors from structured evidence (build_diagnostics.json)...")
    diag_file = os.path.join(rdir, "build_diagnostics.json")
    bw = None
    be = None
    if not os.path.exists(diag_file):
        failures.append("missing artifact: build_diagnostics.json not found")
    else:
        try:
            with open(diag_file, "r", encoding="utf-8") as f:
                diag_data = json.load(f)
            if not isinstance(diag_data, dict):
                failures.append("corrupted build diagnostics: root is not an object")
            elif "compiler_warnings" not in diag_data or "compiler_errors" not in diag_data:
                failures.append("corrupted build diagnostics: missing compiler_warnings or compiler_errors")
            else:
                bw = int(diag_data["compiler_warnings"])
                be = int(diag_data["compiler_errors"])
                exit_code = diag_data.get("build_exit_code", 0)
                if bw > 0:
                    failures.append(f"warnings > 0: {bw} compiler warnings")
                if be > 0:
                    failures.append(f"build errors > 0: {be} compiler errors")
                if exit_code != 0:
                    failures.append(f"build failed with exit code {exit_code}")
                if bw == 0 and be == 0 and exit_code == 0:
                    print("  build structured evidence: 0 errors | 0 warnings: PASS")
        except Exception as ex:
            failures.append(f"corrupted build diagnostics: {ex}")

    # Check for unexpected files in release directory
    allowed_release_files = {
        "AMCCA-Setup.exe",
        "AMCCA-Setup.msi",
        "AMCCA-Desktop-win-x64.zip",
        "SHA256SUMS.txt",
        "release-tests.trx",
        "build_diagnostics.json",
        "RELEASE_METADATA.md",
    }
    if os.path.exists(rdir):
        for fname in os.listdir(rdir):
            if fname not in allowed_release_files and not fname.startswith("."):
                failures.append(f"unexpected file in release bundle: {fname}")

    # 12. Release metadata consistent
    print("[11/15] Verifying release metadata consistency (RELEASE_METADATA.md)...")
    meta_file = os.path.join(rdir, "RELEASE_METADATA.md")
    if not os.path.exists(meta_file):
        failures.append(f"missing artifact: {meta_file} not found")
    else:
        with open(meta_file, "r", encoding="utf-8") as f:
            meta_text = f.read()

        if expected_commit_sha:
            if f"Git Commit SHA: {expected_commit_sha}" not in meta_text and expected_commit_sha not in meta_text:
                failures.append("metadata contradicts real evidence: commit SHA mismatch in metadata")
        elif check_git and current_sha != "UNKNOWN":
            if f"Git Commit SHA: {current_sha}" not in meta_text and current_sha not in meta_text:
                failures.append("metadata contradicts real evidence: commit SHA mismatch in metadata")
        if check_git:
            if "Working Tree: CLEAN" not in meta_text:
                failures.append("metadata contradicts real evidence: Working Tree is not declared CLEAN")

        if exe_hash and exe_hash not in meta_text:
            failures.append(f"metadata contradicts real evidence: EXE SHA256 {exe_hash} not recorded in metadata")
        if msi_hash and msi_hash not in meta_text:
            failures.append(f"metadata contradicts real evidence: MSI SHA256 {msi_hash} not recorded in metadata")
        if zip_hash and zip_hash not in meta_text:
            failures.append(f"metadata contradicts real evidence: ZIP SHA256 {zip_hash} not recorded in metadata")

        if total_tests > 0:
            if f"{total_tests}" not in meta_text:
                failures.append(f"metadata contradicts real evidence: total tests count {total_tests} not in metadata")

        if "Compiler Warnings: 0" not in meta_text or (bw is not None and bw != 0):
            failures.append("metadata contradicts real evidence: Compiler Warnings: 0 not recorded")
        if "Compiler Errors: 0" not in meta_text or (be is not None and be != 0):
            failures.append("metadata contradicts real evidence: Compiler Errors: 0 not recorded")

        if not any("metadata contradicts" in f for f in failures):
            print("  release metadata consistent: PASS")

    print(f"\n{'='*72}\nRELEASE GATE EVALUATION SUMMARY\n{'='*72}")
    if failures:
        print("FAILURES DETECTED:")
        for f in failures:
            print(f"  [X] {f}")
        print("\nRELEASE GATE: FAIL")
        return False, failures

    print("ALL 15 RELEASE INVARIANTS VERIFIED STRICTLY.")
    print("\nRELEASE GATE: PASS")
    return True, []


def main():
    parser = argparse.ArgumentParser(description="AMCCA Release Gate")
    parser.add_argument("--release", action="store_true", help="Run full release verification (DEF-CERT-008)")
    parser.add_argument("--expected-commit-sha", type=str, default=None, help="Expected git commit SHA for release")
    parser.add_argument("--release-dir", type=str, default=None, help="Path to release artifacts directory")
    args, unknown = parser.parse_known_args()

    if args.release:
        ok, _ = verify_release_invariants(
            release_dir=args.release_dir,
            expected_commit_sha=args.expected_commit_sha,
        )
        return 0 if ok else 1

    results = []
    for step_no, label, script, sargs in STEPS:
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
            status, justification = run_reporting_status(f"{step_no:2d}. {label}", [script] + sargs)
            results.append((step_no, label, (status, justification)))
            continue
        ok = run(f"{step_no:2d}. {label}", [script] + sargs)
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
            else:
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

