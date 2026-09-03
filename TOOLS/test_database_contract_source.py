#!/usr/bin/env python3
"""
V31.1.1 (D-DUP-01) meta-test: proves the DDL-consuming tests actually load the
canonical generated DDL, and proves that DDL is load-bearing (removing a real
CHECK constraint changes test outcomes).

(a) Static inspection: TOOLS/test_synthetic_disclosure.py and
    TOOLS/test_platform_evidence.py must not contain a literal "CREATE TABLE" in
    their own source -- their DDL must come from generate_artifacts.build_canonical_ddl(),
    not a local copy. This is exactly the class of defect this whole phase exists
    to close, and this check is what keeps it closed.

(b) Dynamic proof: take the real canonical DDL, programmatically strip a known
    CHECK clause from an in-memory copy (the canonical source on disk is never
    touched), and prove that at least one of the existing test fixtures that used
    to be rejected is now accepted -- i.e. the constraint we stripped was actually
    doing something, not decorative.
"""
import os, re, sqlite3, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(ROOT, "TOOLS")
sys.path.insert(0, TOOLS)
import generate_artifacts as ga

DDL_CONSUMING_TESTS = ["test_synthetic_disclosure.py", "test_platform_evidence.py"]


def check_no_literal_ddl_in_tests():
    ok = True
    for fn in DDL_CONSUMING_TESTS:
        src = open(os.path.join(TOOLS, fn), encoding="utf-8").read()
        has_literal = "CREATE TABLE" in src
        has_import = "generate_artifacts" in src and "build_canonical_ddl" in src
        line_ok = (not has_literal) and has_import
        ok = ok and line_ok
        print(("PASS" if line_ok else "FAIL") +
              f"  {fn} sources its DDL from generate_artifacts.build_canonical_ddl "
              f"(literal CREATE TABLE present: {has_literal}; imports the builder: {has_import})")
    return ok


def _split_top_level_clauses(body):
    """Split the column/constraint list of a CREATE TABLE(...) body on top-level
    commas only (commas nested inside parentheses, e.g. inside CHECK(...) or an
    IN (...) list, are not split points)."""
    clauses, depth, start = [], 0, 0
    for i, ch in enumerate(body):
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
        elif ch == "," and depth == 0:
            clauses.append(body[start:i])
            start = i + 1
    clauses.append(body[start:])
    return clauses


def _strip_first_check(sql, marker):
    """Remove exactly one top-level CHECK(...) clause (the one whose text contains
    `marker`) from a CREATE TABLE statement, in memory only. Returns the mutated SQL,
    reassembled with correct comma placement regardless of clause position."""
    m = re.match(r"^(CREATE TABLE \w+ \()(.*)(\)\s*;?\s*)$", sql, re.S)
    assert m, "DDL did not match the expected CREATE TABLE shape"
    head, body, tail = m.group(1), m.group(2), m.group(3)

    clauses = _split_top_level_clauses(body)
    kept = [c for c in clauses if not (c.strip().startswith("CHECK") and marker in c)]
    assert len(kept) == len(clauses) - 1, \
        f"expected to strip exactly one top-level CHECK clause containing {marker!r}, " \
        f"stripped {len(clauses) - len(kept)}"

    return head + ",".join(c.strip() for c in kept) + "\n" + tail


def check_stripped_constraint_is_load_bearing():
    """Strip the VERIFIED-evidence CHECK from a scratch copy of `publications` and prove
    a case that test_synthetic_disclosure.py asserts MUST fail now PASSES -- proving the
    real constraint (not a decorative comment) is what rejected it before."""
    original_sql = ga.build_canonical_ddl()["publications"]
    mutated_sql = _strip_first_check(original_sql, "evidence_source IN")
    assert mutated_sql != original_sql, "mutation did not change the DDL"
    assert "evidence_source IN" not in mutated_sql, "CHECK clause was not actually removed"

    sd_sql = ga.build_canonical_ddl()["synthetic_declarations"]

    # A row that MUST be rejected against the real contract (VERIFIED with a
    # non-authoritative-looking / unset evidence_source is covered elsewhere; here we
    # use the concrete regression case from test_synthetic_disclosure.py: VERIFIED
    # with a label required but NOT applied -- that one is guarded by a DIFFERENT
    # CHECK, so use the evidence-authority regression case instead, which IS guarded
    # only by the clause we just stripped: VERIFIED via a non-authoritative source
    # would fail on evidence_source IN (...) alone. We build that row directly.
    row_sql = (
        "INSERT INTO publications (id, state, external_id, evidence_source, "
        "evidence_retrieved_at, synthetic_declaration_id, platform_label_required, "
        "synthetic_label_applied) VALUES "
        "('p-mutation-probe','VERIFIED','ext1','SOME_NON_AUTHORITATIVE_SOURCE',"
        "'2026-09-02T10:00:00+00:00',NULL,0,0)"
    )

    def try_insert(sql):
        conn = sqlite3.connect(":memory:")
        conn.executescript(sd_sql)
        conn.executescript(sql)
        try:
            conn.execute(row_sql)
            conn.commit()
            rejected = False
        except sqlite3.IntegrityError:
            rejected = True
        conn.close()
        return rejected

    rejected_by_real_contract = try_insert(original_sql)
    rejected_by_mutated_contract = try_insert(mutated_sql)

    flipped = rejected_by_real_contract and not rejected_by_mutated_contract
    print(("PASS" if flipped else "FAIL") +
          "  stripping the evidence-authority CHECK from a scratch copy of the DDL "
          f"flips a case from REJECTED ({rejected_by_real_contract}) to ACCEPTED "
          f"({not rejected_by_mutated_contract}) -- the constraint is load-bearing, "
          "not decorative")

    # The canonical source on disk must be untouched by any of this.
    unaffected = ga.build_canonical_ddl()["publications"] == original_sql
    print(("PASS" if unaffected else "FAIL") +
          "  canonical DDL source is unaffected by the mutation (mutated a scratch copy only)")

    return flipped and unaffected


def run():
    ok1 = check_no_literal_ddl_in_tests()
    ok2 = check_stripped_constraint_is_load_bearing()
    return 0 if (ok1 and ok2) else 1


if __name__ == "__main__":
    sys.exit(run())
