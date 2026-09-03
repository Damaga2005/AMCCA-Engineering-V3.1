#!/usr/bin/env python3
"""V31-05 regression test: Decimal precision, and a static AST guard against float() in
any money-adjacent tooling code."""
import ast, os, sys
from decimal import Decimal

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def test_decimal_precision():
    a, b = Decimal("0.100000"), Decimal("0.200000")
    ok = (a + b) == Decimal("0.300000")
    print(("PASS" if ok else "FAIL") + "  0.100000 + 0.200000 == 0.300000 via Decimal")
    return ok


def test_no_float_in_tooling():
    tools_dir = os.path.join(ROOT, "TOOLS")
    offenders = []
    for fn in sorted(os.listdir(tools_dir)):
        if not fn.endswith(".py"):
            continue
        src = open(os.path.join(tools_dir, fn), encoding="utf-8").read()
        tree = ast.parse(src, filename=fn)
        for node in ast.walk(tree):
            if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == "float":
                offenders.append(f"{fn}:{node.lineno}")
    ok = not offenders
    print(("PASS" if ok else "FAIL") + f"  no float() calls in TOOLS/*.py (found: {offenders})")
    return ok


def run():
    ok = test_decimal_precision() and test_no_float_in_tooling()
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(run())
