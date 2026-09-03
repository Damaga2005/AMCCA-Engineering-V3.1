#!/usr/bin/env python3
"""
V31.1.1 (D-DUP-01) regression guard: statically scans TOOLS/test_*.py for a
hand-copied SQL contract (a literal `CREATE TABLE` or `CHECK (` in the test's own
source, rather than loaded from generate_artifacts.build_canonical_ddl()). Fails if
found outside ALLOWLIST.

ALLOWLIST is intentionally empty after the V31.1.1 rewrite of
test_synthetic_disclosure.py and test_platform_evidence.py (previously the only
offenders). Keeping it empty -- rather than deleting this test -- is the point:
a future test that reaches for a quick hand-written CREATE TABLE instead of
generate_artifacts.build_canonical_ddl() fails the build immediately.
"""
import os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(ROOT, "TOOLS")

# Files exempt from this scan entirely:
#  - the generator itself: it is the canonical source, it is SUPPOSED to contain
#    CREATE TABLE / CHECK (
#  - this test and its sibling meta-test: they talk ABOUT the pattern in prose/
#    regex literals in order to detect it, which is not the same as embedding a
#    hand-written contract to validate data against.
#  - test_mutations.py: its mutation-3 case discusses "CHECK (" constraints in
#    prose/docstrings while proving them load-bearing, and its one DB-layer
#    assertion loads DDL from generate_artifacts.build_canonical_ddl() like every
#    other DDL-consuming test -- it contains no hand-copied CREATE TABLE.
EXEMPT_FILES = {"generate_artifacts.py", "test_no_contract_duplication.py",
                 "test_database_contract_source.py", "test_mutations.py"}

# ALLOWLIST: {filename: reason}. Intentionally empty -- see module docstring.
ALLOWLIST = {}

# (?<![A-Za-z0-9_]) avoids matching "CHECK (" as a false positive inside an
# identifier like POST_PUBLISH_CHECK (a value name, not a SQL CHECK constraint).
PATTERN = re.compile(r"CREATE TABLE|(?<![A-Za-z0-9_])CHECK\s*\(")


def run():
    ok = True
    scanned = 0
    for fn in sorted(os.listdir(TOOLS)):
        if not (fn.startswith("test_") and fn.endswith(".py")):
            continue
        if fn in EXEMPT_FILES:
            continue
        scanned += 1
        src = open(os.path.join(TOOLS, fn), encoding="utf-8").read()
        hit = PATTERN.search(src)
        if hit and fn in ALLOWLIST:
            print(f"PASS  {fn}: literal SQL contract present but allowlisted "
                  f"({ALLOWLIST[fn]})")
        elif hit:
            print(f"FAIL  {fn}: contains a literal {hit.group(0)!r} at offset {hit.start()} -- "
                  f"a test must load its DDL from generate_artifacts.build_canonical_ddl(), "
                  f"not hand-copy the contract (V31.1.1 D-DUP-01)")
            ok = False
        else:
            print(f"PASS  {fn}: no hand-copied SQL contract")

    print("-" * 72)
    print(f"scanned {scanned} test files; allowlist has {len(ALLOWLIST)} entries "
          f"({'expected empty after the V31.1.1 rewrite' if not ALLOWLIST else 'see reasons above'})")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(run())
