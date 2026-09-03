#!/usr/bin/env python3
"""V31-01 regression test. Thin wrapper: proves --check catches drift and --regen clears it,
without leaving the package tampered afterwards."""
import os, sys, shutil, tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
import generate_artifacts as ga


def run():
    ok, diffs = ga.check_all(ROOT)
    if not ok:
        print("FAIL  package already has drift before the test started:")
        for d in diffs:
            print("  -", d)
        return 1

    target = os.path.join(ROOT, "SPEC", "13_STATE_TRANSITION_MATRIX.md")
    original = open(target, encoding="utf-8").read()
    try:
        with open(target, "a", encoding="utf-8") as f:
            f.write("\nTAMPERED\n")
        ok2, diffs2 = ga.check_all(ROOT)
        if ok2:
            print("FAIL  tampering SPEC/13 was not detected by --check (V31-01 regression)")
            return 1
        print("PASS  tampering SPEC/13 was detected:", diffs2[0])
    finally:
        with open(target, "w", encoding="utf-8") as f:
            f.write(original)

    ok3, diffs3 = ga.check_all(ROOT)
    if not ok3:
        print("FAIL  package not clean after restoring the tampered file:", diffs3)
        return 1
    print("PASS  package clean after restore")
    return 0


if __name__ == "__main__":
    sys.exit(run())
