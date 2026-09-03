#!/usr/bin/env python3
"""
V31.1.2 (reviewer item 1 / V31.1.1-02): SV must have exactly one source of truth --
TOOLS/generate_artifacts.py. A hardcoded `SV = "3.1.0"` (or similar) anywhere else in
TOOLS/*.py is a second copy of the package version that can silently drift from the
canonical one the moment the package version bumps and one call site is missed.

This is a static scan, not a semantic one: it does not care whether the duplicated
literal currently happens to equal generate_artifacts.SV, only that a second
assignment to a module-level name `SV` exists at all outside the canonical generator.
Even a duplicate that currently agrees is the defect -- it is a fork waiting to
happen, and "it happens to match today" is exactly the false confidence this test
exists to remove.

    python TOOLS/test_no_duplicated_version_constants.py
"""
import ast, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(ROOT, "TOOLS")
CANONICAL = "generate_artifacts.py"


def _is_string_literal(value_node):
    """True for a bare string-literal RHS (what `SV = "3.1.0"` / `SV = '3.1.0'` parses
    to, including an implicitly-concatenated literal like `"3" "." "1.0"`). False for
    an RHS that *references* something else, such as `SV = ga.SV` or `SV = get_sv()`
    -- those are not a second source of truth, they are the fix this test wants to see."""
    return isinstance(value_node, ast.Constant) and isinstance(value_node.value, str)


def find_top_level_sv_assignments(path):
    """Returns a list of (lineno, source_line) for every top-level (module-scope)
    assignment of a NAME called SV to a string literal. Using the AST (rather than a
    text regex) means this can't be fooled by SV appearing inside a comment or a
    docstring, and correctly distinguishes a real duplicated literal (`SV = "3.1.0"`)
    from the fix (`SV = ga.SV`, an attribute reference back to the canonical
    generator) -- a regex on `SV = ['"]` alone cannot tell those apart."""
    with open(path, encoding="utf-8") as f:
        src = f.read()
    tree = ast.parse(src, filename=os.path.basename(path))
    hits = []
    for node in tree.body:  # module top level only -- SV as a local variable inside a
                            # function is not a second source of truth for anything
        if isinstance(node, ast.Assign):
            targets, value = node.targets, node.value
        elif isinstance(node, ast.AnnAssign) and node.value is not None:
            targets, value = [node.target], node.value
        else:
            continue
        if not _is_string_literal(value):
            continue
        for t in targets:
            if isinstance(t, ast.Name) and t.id == "SV":
                line = src.splitlines()[node.lineno - 1].strip()
                hits.append((node.lineno, line))
    return hits


def run():
    offenders = []
    checked = []
    for fn in sorted(os.listdir(TOOLS)):
        if not fn.endswith(".py") or fn == CANONICAL:
            continue
        path = os.path.join(TOOLS, fn)
        checked.append(fn)
        for lineno, line in find_top_level_sv_assignments(path):
            offenders.append(f"TOOLS/{fn}:{lineno}: {line}")

    print(f"scanned {len(checked)} TOOLS/*.py files (excluding {CANONICAL}) for a duplicated "
          f"top-level `SV = ...` assignment")
    if offenders:
        print("FAIL  version.no_duplicated_SV_constant_outside_canonical_generator")
        for o in offenders:
            print(f"        {o}")
        print(f"        fix: import SV from {CANONICAL} instead of assigning it locally "
              f"(see TOOLS/conformance_tests.py or TOOLS/validate_package.py for the pattern)")
        return 1

    print("PASS  version.no_duplicated_SV_constant_outside_canonical_generator "
          f"(SV is defined exactly once, in TOOLS/{CANONICAL})")
    return 0


if __name__ == "__main__":
    sys.exit(run())
