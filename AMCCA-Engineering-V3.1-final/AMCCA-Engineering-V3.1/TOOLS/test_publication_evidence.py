#!/usr/bin/env python3
"""V31-06 / V31-07 regression test: publication VERIFIED requires authoritative evidence
and, when a label is required, requires it to actually be applied."""
import json, os, sys
from jsonschema import Draft202012Validator, FormatChecker

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "TOOLS"))
import generate_artifacts as ga  # V31.1.2 (item 1): SV comes from the canonical
                                  # generator, never a second hardcoded literal here.
SV = ga.SV
ULID = "01J8ZQ4T7K9WPX2MNVBCDEFGHJ"
ULID2 = "01J8ZQ4T7K9WPX2MNVBCDEFGHK"
ULID3 = "01J8ZQ4T7K9WPX2MNVBCDEFGHM"
TS = "2026-09-02T10:00:00+00:00"

BASE = {"schema_version": SV, "id": ULID, "production_id": ULID2,
        "platform": "youtube", "account_id": ULID, "content_version_id": ULID,
        "state": "VERIFIED", "required": True, "platform_label_required": False,
        "idempotency_key": "pub-youtube-01J8ZQ4T7K9WPX2MNVBCDEFGHJ-v1",
        "created_at": TS, "updated_at": TS, "external_id": "abc123"}

CASES = [
    ("VERIFIED without evidence_source", {}, False),
    ("VERIFIED with OFFICIAL_API", {"evidence_source": "OFFICIAL_API", "evidence_retrieved_at": TS}, True),
    ("VERIFIED with OFFICIAL_DASHBOARD", {"evidence_source": "OFFICIAL_DASHBOARD", "evidence_retrieved_at": TS}, True),
    ("VERIFIED with OPERATOR_CONFIRMATION", {"evidence_source": "OPERATOR_CONFIRMATION", "evidence_retrieved_at": TS}, True),
    ("VERIFIED with POST_PUBLISH_CHECK (a resolving URL)", {"evidence_source": "POST_PUBLISH_CHECK", "evidence_retrieved_at": TS}, False),
    ("VERIFIED + label required + applied", {"evidence_source": "OFFICIAL_API", "evidence_retrieved_at": TS,
     "platform_label_required": True, "synthetic_declaration_id": ULID3, "synthetic_label_applied": True}, True),
    ("VERIFIED + label required + NOT applied", {"evidence_source": "OFFICIAL_API", "evidence_retrieved_at": TS,
     "platform_label_required": True, "synthetic_declaration_id": ULID3, "synthetic_label_applied": False}, False),
    ("VERIFIED + label required + no declaration link", {"evidence_source": "OFFICIAL_API", "evidence_retrieved_at": TS,
     "platform_label_required": True, "synthetic_label_applied": True}, False),
]


def run():
    schema = json.load(open(os.path.join(ROOT, "SCHEMAS", "publication.schema.json"), encoding="utf-8"))
    v = Draft202012Validator(schema, format_checker=FormatChecker())
    ok = True
    for name, extra, should_pass in CASES:
        inst = {**BASE, **extra}
        errs = list(v.iter_errors(inst))
        passed = not errs
        line_ok = passed == should_pass
        ok = ok and line_ok
        print(("PASS" if line_ok else "FAIL") + f"  {name}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(run())
