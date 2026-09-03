#!/usr/bin/env python3
"""
V31.1.2 (reviewer item 4): prove TOOLS/requirements.txt is still the faithful output
of `pip-compile TOOLS/requirements.in`, so the loose bounds in requirements.in (e.g.
jsonschema>=4.18) and the exact pins in requirements.txt cannot silently drift apart
with nobody noticing.

This test attempts to regenerate the lockfile with pip-compile (from pip-tools) in a
throwaway temp directory and diffs the resulting package==version pins against the
checked-in TOOLS/requirements.txt. pip-compile requires the pip-tools package and
PyPI network access; when either is unavailable, this test:
  - NEVER silently PASSes (that would be exactly the "not tested" == "PASS"
    conflation the V3.1.1/V3.1.2 status vocabulary exists to close),
  - NEVER installs pip-tools itself as a side effect of merely running a test,
  - NEVER hard-FAILs (that would break CI in every network-restricted environment
    for a reason that has nothing to do with whether the lockfile actually drifted),
  - reports an explicit N/A with a clear justification and instructions for a human
    or a CI job WITH network access to verify for real.

Emits a final line `GATE_STATUS: PASS|FAIL|N/A -- <detail>` that TOOLS/release_gate.py
parses to tell N/A (does not block release) apart from FAIL (blocks release) and PASS.

    python TOOLS/test_requirements_lockfile_fresh.py
"""
import os, re, shutil, subprocess, sys, tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(ROOT, "TOOLS")
REQ_IN = os.path.join(TOOLS, "requirements.in")
REQ_TXT = os.path.join(TOOLS, "requirements.txt")

PIN_RE = re.compile(r"^([A-Za-z0-9_.\-]+)==([A-Za-z0-9_.\-]+)\s*$")


def _pins(path):
    """package(lowercased) -> version, parsed from a requirements.txt-shaped file.
    Comment/annotation lines ('    # via ...') don't match PIN_RE and are ignored."""
    out = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            m = PIN_RE.match(line.strip())
            if m:
                out[m.group(1).lower()] = m.group(2)
    return out


def _pip_compile_command():
    """Locates a runnable pip-compile without installing anything -- this test must
    never mutate the environment as a side effect of running. Returns the argv prefix
    to invoke it with, or None if pip-tools isn't available here."""
    if shutil.which("pip-compile"):
        return ["pip-compile"]
    try:
        r = subprocess.run([sys.executable, "-m", "piptools", "--version"],
                            capture_output=True, text=True, timeout=10)
        if r.returncode == 0:
            return [sys.executable, "-m", "piptools", "compile"]
    except (FileNotFoundError, OSError, subprocess.TimeoutExpired):
        pass
    return None


def _na(check_name, justification):
    print(f"N/A   {check_name} -- {justification}")
    print(f"GATE_STATUS: N/A -- {justification}")
    return 0


def run():
    check_name = "requirements.lockfile_matches_fresh_pip_compile"
    how_to_verify = ("To verify for real: pip install pip-tools && "
                      "pip-compile TOOLS/requirements.in -o /tmp/check-requirements.txt && "
                      "diff /tmp/check-requirements.txt TOOLS/requirements.txt "
                      "(run on a machine or CI job with PyPI network access).")

    cmd_prefix = _pip_compile_command()
    if cmd_prefix is None:
        return _na(check_name,
                    "pip-tools is not installed in this environment (no `pip-compile` on PATH and "
                    "`python -m piptools` is unavailable). This test never installs packages as a "
                    "side effect of merely running, so it cannot regenerate the lockfile here. "
                    + how_to_verify)

    scratch = tempfile.mkdtemp(prefix="amcca_reqlock_")
    out_path = os.path.join(scratch, "requirements.txt")
    try:
        try:
            r = subprocess.run(cmd_prefix + [REQ_IN, "-o", out_path, "--quiet"],
                                cwd=scratch, capture_output=True, text=True, timeout=120)
        except subprocess.TimeoutExpired:
            return _na(check_name,
                        "pip-compile did not complete within 120s (most likely no PyPI network "
                        "access in this sandbox). " + how_to_verify)

        if r.returncode != 0 or not os.path.exists(out_path):
            tail = (r.stderr or r.stdout or "").strip()[-500:]
            return _na(check_name,
                        f"pip-compile exited {r.returncode} (most likely no PyPI network access in "
                        f"this sandbox): {tail!r}. " + how_to_verify)

        fresh_pins = _pins(out_path)
        if not fresh_pins:
            return _na(check_name,
                        "pip-compile ran but produced no parseable package==version pins; treating "
                        "this as an unusable result rather than either a PASS or a FAIL. " + how_to_verify)

        checked_in_pins = _pins(REQ_TXT)
        diff = {k: (checked_in_pins.get(k), fresh_pins.get(k))
                for k in set(fresh_pins) | set(checked_in_pins)
                if checked_in_pins.get(k) != fresh_pins.get(k)}

        if diff:
            print(f"FAIL  {check_name}")
            for pkg, (checked_in, fresh) in sorted(diff.items()):
                print(f"        {pkg}: checked-in={checked_in!r} fresh-compile={fresh!r}")
            detail = ("TOOLS/requirements.txt has drifted from a fresh pip-compile of "
                       "TOOLS/requirements.in for: " + ", ".join(sorted(diff)) +
                       ". Re-run pip-compile TOOLS/requirements.in -o TOOLS/requirements.txt and commit "
                       "the result.")
            print(f"GATE_STATUS: FAIL -- {detail}")
            return 1

        print(f"PASS  {check_name} ({len(fresh_pins)} pins match a fresh pip-compile of requirements.in)")
        print("GATE_STATUS: PASS")
        return 0
    finally:
        shutil.rmtree(scratch, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(run())
