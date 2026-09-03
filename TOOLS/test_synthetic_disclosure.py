#!/usr/bin/env python3
"""V31-07 regression test: the synthetic-label gate is structural at the database layer,
not merely something the preflight code path is supposed to check.

V31.1.1: this test used to hand-copy its own guess at the publications CHECK
constraints as a literal DDL string. That meant it could pass even if the real
contract in generate_artifacts.py drifted or was weakened, because the test was
never actually exercising the canonical contract. It now loads the real,
generated DDL via generate_artifacts.build_canonical_ddl() -- the single source
of truth also used to build SCHEMAS/schema.sql and SCHEMAS/publication.schema.json
-- so there is exactly one place this constraint is defined."""
import os, sqlite3, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import generate_artifacts as ga

DDL = ga.build_canonical_ddl()["synthetic_declarations"] + "\n" + ga.build_canonical_ddl()["publications"]

TS = "2026-09-02T10:00:00+00:00"

CASES = [
    # name, (id, state, external_id, evidence_source, evidence_retrieved_at,
    #        synthetic_declaration_id, platform_label_required, synthetic_label_applied), decl_row_first, should_pass
    ("PROCESSING with no declaration needed",
     ("p1", "PROCESSING", None, None, None, None, 0, 0), False, True),
    ("VERIFIED, no label required",
     ("p2", "VERIFIED", "ext1", "OFFICIAL_API", TS, None, 0, 0), False, True),
    ("VERIFIED, label required, applied, declaration linked",
     ("p3", "VERIFIED", "ext2", "OFFICIAL_API", TS, "d1", 1, 1), True, True),
    ("VERIFIED, label required, NOT applied -- must fail",
     ("p4", "VERIFIED", "ext3", "OFFICIAL_API", TS, "d2", 1, 0), True, False),
    ("VERIFIED, label required, no declaration link -- must fail",
     ("p5", "VERIFIED", "ext4", "OFFICIAL_API", TS, None, 1, 1), False, False),
    ("Non-VERIFIED row with label required but no declaration -- must fail (structural, not state-gated)",
     ("p6", "PROCESSING", None, None, None, None, 1, 0), False, False),
    ("Attempt to un-apply a label after VERIFIED (caller tries state=VERIFIED, applied=0 again) -- must fail",
     ("p7", "VERIFIED", "ext5", "OFFICIAL_API", TS, "d3", 1, 0), True, False),
]


def run():
    conn = sqlite3.connect(":memory:")
    conn.executescript(DDL)
    ok = True
    for name, row, needs_decl, should_pass in CASES:
        try:
            if needs_decl and row[5]:
                conn.execute("INSERT OR IGNORE INTO synthetic_declarations (id) VALUES (?)", (row[5],))
            conn.execute("INSERT INTO publications (id, state, external_id, evidence_source, "
                         "evidence_retrieved_at, synthetic_declaration_id, platform_label_required, "
                         "synthetic_label_applied) VALUES (?,?,?,?,?,?,?,?)", row)
            conn.commit()
            passed = True
        except sqlite3.IntegrityError:
            conn.rollback()
            passed = False
        line_ok = passed == should_pass
        ok = ok and line_ok
        print(("PASS" if line_ok else "FAIL") + f"  {name}")
    conn.close()
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(run())
