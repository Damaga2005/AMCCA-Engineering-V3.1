#!/usr/bin/env python3
"""V31-09 regression test: a secondary-sourced evidence_source can never sustain
VERIFIED on platform_capabilities. Since this table has no dedicated JSON schema
(SPEC/11 documents it as a database CHECK), this test builds the real constraint in
an in-memory SQLite database and proves it rejects what it must reject.

V31.1.1: loads the real, generated DDL via generate_artifacts.build_canonical_ddl()
instead of a hand-copied literal, so a change to the real constraint (in either
direction) is what this test observes."""
import os, sqlite3, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import generate_artifacts as ga

DDL = ga.build_canonical_ddl()["platform_capabilities"]

CASES = [
    ("DISCOVERED via a blog", ("youtube", "acct1", "upload_video", "DISCOVERED", "third_party_blog", "2026-09-02"), True),
    ("VERIFIED via OFFICIAL_API", ("youtube", "acct1", "set_thumbnail", "VERIFIED", "OFFICIAL_API", "2026-09-02"), True),
    ("VERIFIED via a blog (must fail)", ("youtube", "acct1", "schedule_publish", "VERIFIED", "third_party_blog", "2026-09-02"), False),
    ("VERIFIED via an agency article (must fail)", ("tiktok", "acct2", "apply_synthetic_label", "VERIFIED", "agency_article", "2026-09-02"), False),
    ("VERIFIED via DIRECT_PLATFORM_PROBE", ("instagram", "acct3", "read_metrics", "VERIFIED", "DIRECT_PLATFORM_PROBE", "2026-09-02"), True),
]


def run():
    conn = sqlite3.connect(":memory:")
    conn.execute(DDL)
    ok = True
    for name, row, should_pass in CASES:
        try:
            conn.execute("INSERT INTO platform_capabilities "
                         "(platform, account_id, capability, status, evidence_source, verified_at) "
                         "VALUES (?,?,?,?,?,?)", row)
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
