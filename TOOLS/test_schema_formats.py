#!/usr/bin/env python3
"""V31-02 regression test: format:date-time is actually enforced, not merely declared."""
import sys
from jsonschema import Draft202012Validator, FormatChecker

POSITIVES = ["2026-09-03T08:41:00Z", "2026-09-03T08:41:00+00:00", "2026-09-03T10:41:00+02:00"]
NEGATIVES = ["NOT-A-DATE", "2026-99-99T00:00:00Z", "2026-09-03",
             "2026-09-03T99:99:99Z", "2026-09-03 10:00:00"]


def run():
    fc = FormatChecker()
    if "date-time" not in fc.checkers:
        print("FAIL  rfc3339-validator not installed; date-time format is not enforced (D-032)")
        return 1

    schema = {"type": "object", "properties": {"t": {"type": "string", "format": "date-time"}}}
    v = Draft202012Validator(schema, format_checker=fc)

    ok = True
    for p in POSITIVES:
        errs = list(v.iter_errors({"t": p}))
        status = "PASS" if not errs else "FAIL"
        if errs:
            ok = False
        print(f"{status}  accept {p!r}")
    for n in NEGATIVES:
        errs = list(v.iter_errors({"t": n}))
        status = "PASS" if errs else "FAIL"
        if not errs:
            ok = False
        print(f"{status}  reject {n!r}")

    # regression guard: a validator built WITHOUT format_checker must NOT enforce this
    # (documents why format_checker= is mandatory everywhere, not a redundant safeguard)
    v_naive = Draft202012Validator(schema)
    naive_errs = list(v_naive.iter_errors({"t": "NOT-A-DATE"}))
    if naive_errs:
        print("NOTE  unexpectedly, a validator without format_checker rejected an invalid date "
              "(jsonschema version behaviour may have changed; format_checker is still required)")
    else:
        print("PASS  confirmed: without format_checker, invalid dates pass silently -- "
              "this is exactly why V31-02 requires format_checker at every construction site")

    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(run())
