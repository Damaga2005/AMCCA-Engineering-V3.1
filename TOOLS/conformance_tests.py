#!/usr/bin/env python3
"""
AMCCA contract conformance tests.

A schema that has never rejected anything has not been shown to enforce anything.
Every conditional invariant in SCHEMAS/ gets a positive instance that MUST validate
and a negative instance that MUST fail. Required by SPEC/79 criterion 9.

V3.1 additions (audit applied in full):
  V31-02: date-time format cases, validated with FormatChecker.
  V31-03: automatic conditional-coverage discovery against TOOLS/conditional_coverage.json.
          A schema conditional with no declared coverage fails the suite with
          "UNCOVERED CONDITIONAL" and exit code 1 -- it is not enough for cases to exist
          and pass; every discovered if/then must be named in the coverage map.
  V31-04: NonNegativeMoney vs SignedMoney cases.
  V31-06: publication VERIFIED tightened -- POST_PUBLISH_CHECK (renamed from
          PUBLIC_URL_CHECK) can never satisfy VERIFIED, only OFFICIAL_API,
          OFFICIAL_DASHBOARD or OPERATOR_CONFIRMATION can.
  V31-07: publication VERIFIED structurally requires platform_label_applied when
          platform_label_required is true, via a real schema conditional, not only
          preflight code.
  V31-09: platform_capabilities.evidence_source restricted set is exercised at the
          database-contract level (see TOOLS/test_platform_evidence.py), since
          platform_capabilities has no dedicated JSON schema; documented here.

    python TOOLS/conformance_tests.py
"""
import collections, json, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCH = os.path.join(ROOT, "SCHEMAS")
sys.path.insert(0, os.path.join(ROOT, "TOOLS"))

try:
    from jsonschema import Draft202012Validator, FormatChecker
except ImportError:
    print("FAIL: jsonschema not installed (pip install jsonschema)")
    sys.exit(1)

import generate_artifacts as ga  # V31.1.2 (item 1): SV comes from the canonical generator,
                                  # never a second hardcoded literal here.

ULID = "01J8ZQ4T7K9WPX2MNVBCDEFGHJ"
ULID2 = "01J8ZQ4T7K9WPX2MNVBCDEFGHK"
ULID3 = "01J8ZQ4T7K9WPX2MNVBCDEFGHM"
TS = "2026-09-02T10:00:00+00:00"
SHA = "a" * 64
SV = ga.SV

CASES = []  # (schema, name, instance, should_pass, why)


def case(schema, name, instance, should_pass, why):
    CASES.append((schema, name, instance, should_pass, why))


# ------------------------------------------------- production: state enum
prod = {"schema_version": SV, "id": ULID, "state": "RESEARCHING",
        "autonomy_mode": "MANUAL", "language": "es-ES",
        "aggregate_version": 0, "created_at": TS, "updated_at": TS}
case("production", "valid production", prod, True, "baseline")
case("production", "state outside the state machine",
     {**prod, "state": "ALMOST_DONE"}, False,
     "V2 had production.status as an unconstrained string; this is the fix")
case("production", "unknown field rejected", {**prod, "vibe": "good"}, False, "additionalProperties: false (D-004)")
case("production", "missing schema_version",
     {k: v for k, v in prod.items() if k != "schema_version"}, False, "D-004")

# ------------------------------------------ production: date-time (V31-02)
case("production", "created_at is a valid RFC3339 timestamp",
     {**prod, "created_at": "2026-09-03T08:41:00Z"}, True, "V31-02 positive")
case("production", "created_at with explicit positive offset",
     {**prod, "created_at": "2026-09-03T10:41:00+02:00"}, True, "V31-02 positive")
case("production", "created_at is not a date at all",
     {**prod, "created_at": "NOT-A-DATE"}, False, "V31-02: FormatChecker must reject this")
case("production", "created_at is date-only, no time",
     {**prod, "created_at": "2026-09-03"}, False, "V31-02: date-time requires a time component")
case("production", "created_at has an invalid month/day",
     {**prod, "created_at": "2026-99-99T00:00:00Z"}, False, "V31-02")
case("production", "created_at uses a space instead of T",
     {**prod, "created_at": "2026-09-03 10:00:00"}, False, "V31-02: RFC3339 requires the T separator")

# ------------------------------------------- publication: I-11 (tightened V31-06), I-18 (V31-07)
pub = {"schema_version": SV, "id": ULID, "production_id": ULID2,
       "platform": "youtube", "account_id": ULID, "content_version_id": ULID,
       "state": "PROCESSING", "required": True, "platform_label_required": False,
       "idempotency_key": "pub-youtube-01J8ZQ4T7K9WPX2MNVBCDEFGHJ-v1",
       "created_at": TS, "updated_at": TS}
case("publication", "processing without evidence is fine", pub, True,
     "evidence is only required at VERIFIED")
case("publication", "VERIFIED with authoritative evidence",
     {**pub, "state": "VERIFIED", "external_id": "abc123",
      "evidence_source": "OFFICIAL_API", "evidence_retrieved_at": TS}, True, "I-11")
case("publication", "VERIFIED via OPERATOR_CONFIRMATION",
     {**pub, "state": "VERIFIED", "external_id": "abc123",
      "evidence_source": "OPERATOR_CONFIRMATION", "evidence_retrieved_at": TS}, True, "I-11")
case("publication", "VERIFIED without evidence",
     {**pub, "state": "VERIFIED"}, False, "I-11: a publication cannot be VERIFIED on optimism")
case("publication", "VERIFIED with POST_PUBLISH_CHECK evidence",
     {**pub, "state": "VERIFIED", "external_id": "abc123",
      "evidence_source": "POST_PUBLISH_CHECK", "evidence_retrieved_at": TS}, False,
     "V31-06: a resolving URL is not authoritative evidence and can never satisfy VERIFIED, "
     "even though POST_PUBLISH_CHECK is a syntactically valid evidence_source value elsewhere")
case("publication", "VERIFIED with external_id but no evidence source",
     {**pub, "state": "VERIFIED", "external_id": "abc123"}, False, "I-11: an id is not evidence")
# V31-07: structural synthetic-label gate
case("publication", "VERIFIED with required label applied",
     {**pub, "state": "VERIFIED", "external_id": "abc123",
      "evidence_source": "OFFICIAL_API", "evidence_retrieved_at": TS,
      "platform_label_required": True, "synthetic_declaration_id": ULID3,
      "synthetic_label_applied": True}, True, "I-18")
case("publication", "VERIFIED with required label not applied",
     {**pub, "state": "VERIFIED", "external_id": "abc123",
      "evidence_source": "OFFICIAL_API", "evidence_retrieved_at": TS,
      "platform_label_required": True, "synthetic_declaration_id": ULID3,
      "synthetic_label_applied": False}, False,
     "V31-07: I-18 is now structural -- VERIFIED is unreachable while a required label is unapplied, "
     "even if the preflight code path that is supposed to prevent it has a bug")
case("publication", "VERIFIED requiring a label with no declaration linked",
     {**pub, "state": "VERIFIED", "external_id": "abc123",
      "evidence_source": "OFFICIAL_API", "evidence_retrieved_at": TS,
      "platform_label_required": True, "synthetic_label_applied": True}, False,
     "V31-07: a required label must be traceable to its synthetic_declaration_id")

# -------------------------------------------------- job: I-05 lease
job = {"schema_version": SV, "id": ULID, "type": "render", "state": "QUEUED",
       "priority": 3, "idempotency_key": "job-render-01J8ZQ4T7K9WPX2MNVBCDEFGHJ",
       "attempt": 0, "max_attempts": 3, "currency": "EUR",
       "correlation_id": ULID, "created_at": TS, "updated_at": TS}
case("job", "queued job without a lease", job, True, "correct")
case("job", "RUNNING with a lease",
     {**job, "state": "RUNNING", "lease_owner": "worker-1", "lease_until": TS}, True, "I-05")
case("job", "LEASED without a lease owner", {**job, "state": "LEASED"}, False,
     "I-05: an executing job always has exactly one lease")
case("job", "priority out of range", {**job, "priority": 9}, False, "SPEC/14")
case("job", "money as a float", {**job, "estimated_cost": 1.5}, False,
     "D-023: money is a decimal string, never a float")
case("job", "money with wrong precision", {**job, "estimated_cost": "1.50"}, False, "D-023")
case("job", "money correctly formatted", {**job, "estimated_cost": "1.500000"}, True, "D-023")
case("job", "negative estimated cost", {**job, "estimated_cost": "-1.000000"}, False,
     "V31-04: estimated_cost is NonNegativeMoney; a negative estimate is meaningless")
case("job", "negative reserved cost", {**job, "reserved_cost": "-0.500000"}, False,
     "V31-04: reserved_cost is NonNegativeMoney")

# ------------------------------------------- tool-run: I-03 intent first
tr = {"schema_version": SV, "run_id": ULID, "tool_id": "gateway.text",
      "tool_version": "1.0.0", "side_effect_class": "READ", "state": "STARTED",
      "input_hash": SHA, "correlation_id": ULID, "started_at": TS}
case("tool-run", "read tool needs no intent", tr, True, "baseline")
case("tool-run", "EXTERNAL_UNSAFE with an intent",
     {**tr, "tool_id": "platform.upload", "side_effect_class": "EXTERNAL_UNSAFE",
      "intent_id": ULID2, "idempotency_key": "pub-1234567890abcdef"}, True, "I-03")
case("tool-run", "EXTERNAL_UNSAFE without an intent",
     {**tr, "side_effect_class": "EXTERNAL_UNSAFE"}, False,
     "I-03: every external mutation has a committed intent before the call")

# ------------------------------------------------- event: D-018 causality
ev = {"schema_version": SV, "event_id": ULID, "event_type": "production.state_changed",
      "aggregate_type": "production", "aggregate_id": ULID2, "aggregate_version": 4,
      "correlation_id": ULID, "causation_id": ULID2, "transition_id": "T-002",
      "occurred_at": TS, "payload": {}}
case("event", "event with correlation and causation", ev, True,
     "D-018 as amended; V2's schema forbade both fields")
case("event", "event without correlation_id",
     {k: v for k, v in ev.items() if k != "correlation_id"}, False, "D-018")
case("event", "malformed event_type", {**ev, "event_type": "ProductionChanged"}, False,
     "dotted lower_snake required")
case("event", "malformed transition_id", {**ev, "transition_id": "T-2"}, False,
     "must match a SPEC/13 transition id shape")
case("event", "occurred_at is not RFC3339", {**ev, "occurred_at": "yesterday"}, False, "V31-02")

# ------------------------------------------------- audit: I-09 no agent actor
au = {"schema_version": SV, "audit_id": ULID, "action": "publish",
      "actor_type": "OPERATOR", "actor_id": "dani", "outcome": "APPROVED",
      "correlation_id": ULID, "occurred_at": TS}
case("audit", "operator as actor", au, True, "baseline")
case("audit", "agent as actor", {**au, "actor_type": "AGENT"}, False,
     "I-09: an agent is never the authority for a protected action")

# ------------------------------------------------- qa: I-19 deterministic verdict
qa = {"schema_version": SV, "report_id": ULID, "production_id": ULID2,
      "artifact_version_id": ULID, "stage": "CONTENT_QA", "overall_score": 8.9,
      "critical_scores": {"factual_accuracy": 9.0, "rights": 10.0,
                          "technical_integrity": 9.5, "audio_intelligibility": 8.8,
                          "visual_integrity": 9.1},
      "verdict": "PASS", "threshold_profile_id": ULID,
      "findings": [{"check_id": "QA-CNT-001", "check_kind": "DETERMINISTIC",
                    "status": "PASS", "severity": "INFO",
                    "responsible_artifact_version_id": ULID}],
      "evaluated_at": TS}
case("qa", "deterministic finding", qa, True, "baseline")
case("qa", "AI-assisted finding is representable as evidence",
     {**qa, "findings": [{**qa["findings"][0], "check_kind": "AI_ASSISTED"}]}, True,
     "I-19: allowed as evidence; the verdict rule is enforced in code, not schema")
case("qa", "missing a critical dimension",
     {**qa, "critical_scores": {k: v for k, v in qa["critical_scores"].items()
                                if k != "rights"}}, False,
     "the dimension set is fixed so a threshold cannot be evaded by renaming")
case("qa", "invented critical dimension",
     {**qa, "critical_scores": {**qa["critical_scores"], "vibes": 10.0}}, False, "same reason")
case("qa", "finding without a responsible artifact",
     {**qa, "findings": [{"check_id": "QA-CNT-001", "check_kind": "DETERMINISTIC",
                          "status": "FAIL", "severity": "HIGH"}]}, False,
     "SPEC/37: without it, rework has no target")

# ------------------------------------------------- claim: evidence required
cl = {"schema_version": SV, "claim_id": ULID, "production_id": ULID2,
      "text": "A stated fact.", "status": "VERIFIED", "materiality": "MATERIAL",
      "sources": [{"source_id": ULID, "url": "https://example.org/a",
                   "retrieved_at": TS, "content_hash": SHA, "trust_tier": "PRIMARY"}],
      "created_at": TS}
case("claim", "claim with a timestamped source", cl, True, "D-014")
case("claim", "claim with no sources", {**cl, "sources": []}, False,
     "minItems 1: a claim without a source is not a claim")
case("claim", "source without retrieved_at",
     {**cl, "sources": [{k: v for k, v in cl["sources"][0].items()
                         if k != "retrieved_at"}]}, False,
     "FACT_CHECKING_POLICY: a source with no retrieval time supports nothing")
case("claim", "source with malformed retrieved_at",
     {**cl, "sources": [{**cl["sources"][0], "retrieved_at": "sometime last week"}]}, False, "V31-02")

# ------------------------------------------------- referral: HTTP_CHECK bar
rf = {"schema_version": SV, "referral_id": ULID, "program_id": ULID2,
      "state": "UNVERIFIED", "validation_method": "HTTP_CHECK",
      "validated_at": TS, "disclosure_required": True, "created_at": TS}
case("referral", "unverified via HTTP check", rf, True, "baseline")
case("referral", "ACTIVE via HTTP_CHECK", {**rf, "state": "ACTIVE"}, False,
     "AFFILIATE_POLICY: a 200 OK proves a URL resolves, nothing more")
case("referral", "ACTIVE via official API with evidence",
     {**rf, "state": "ACTIVE", "validation_method": "OFFICIAL_API",
      "validation_evidence_ref": "dash://program/123"}, True, "correct")
case("referral", "ACTIVE via official API without evidence ref",
     {**rf, "state": "ACTIVE", "validation_method": "OFFICIAL_API"}, False,
     "evidence reference is required to sustain ACTIVE")
case("referral", "DISCOVERED state accepted for a secondary-sourced link",
     {**rf, "state": "DISCOVERED"}, True,
     "V31-09: DISCOVERED is the correct resting state for secondary-sourced findings")

# ------------------------------------------------- analytics: I-12 provenance
an = {"schema_version": SV, "observation_id": ULID, "production_id": ULID2,
      "publication_id": ULID, "metric": "views", "value": 1200.0,
      "provenance": "API_MEASURED", "observed_at": TS}
case("analytics", "measured observation", an, True, "I-12")
case("analytics", "observation without provenance",
     {k: v for k, v in an.items() if k != "provenance"}, False,
     "I-12: provenance is what keeps forecasts out of the ledger")

# ------------------------------------------------- rights
ri = {"schema_version": SV, "rights_id": ULID, "production_id": ULID2,
      "asset_hash": SHA, "status": "GREEN", "license": "CC0-1.0",
      "provenance": "GENERATED", "commercial_use": "ALLOWED",
      "modification": "ALLOWED", "attribution_required": False, "evaluated_at": TS}
case("rights", "green generated asset", ri, True, "baseline")
case("rights", "invented rights status", {**ri, "status": "PROBABLY_FINE"}, False, "closed enum")

# ------------------------------------------------- cost-event (V31-04)
co = {"schema_version": SV, "cost_event_id": ULID, "production_id": ULID2,
      "kind": "SETTLEMENT", "amount": "0.421300", "currency": "EUR",
      "reconciliation_state": "RECONCILED", "pricing_snapshot_id": ULID, "occurred_at": TS}
case("cost-event", "settled cost", co, True, "baseline")
case("cost-event", "float amount", {**co, "amount": 0.4213}, False, "D-023")
case("cost-event", "amount without required precision", {**co, "amount": "0.42"}, False, "D-023")
case("cost-event", "negative amount for a non-adjustment kind",
     {**co, "amount": "-0.421300"}, False,
     "V31-04: SETTLEMENT must be non-negative; only ADJUSTMENT may be signed")
case("cost-event", "negative amount for an ADJUSTMENT",
     {**co, "kind": "ADJUSTMENT", "amount": "-2.500000"}, True,
     "V31-04: an accounting adjustment (e.g. a provider refund) may be signed")
case("cost-event", "positive amount for an ADJUSTMENT",
     {**co, "kind": "ADJUSTMENT", "amount": "2.500000"}, True,
     "V31-04: an ADJUSTMENT may also be a positive correction")

# ------------------------------------------------- manifest
mf = {"schema_version": SV, "manifest_id": ULID, "production_id": ULID2, "sealed": True,
      "artifacts": [{"artifact_version_id": ULID, "artifact_kind": "RENDER",
                     "sha256": SHA, "bytes": 1024, "path": "artifacts/aa/x.mp4",
                     "state": "CURRENT", "depends_on": [ULID2]}],
      "created_at": TS}
case("manifest", "sealed manifest", mf, True, "baseline")
case("manifest", "empty manifest", {**mf, "artifacts": []}, False, "minItems 1")
case("manifest", "malformed hash",
     {**mf, "artifacts": [{**mf["artifacts"][0], "sha256": "deadbeef"}]}, False,
     "64 hex characters required")

# ------------------------------------------------- config (V31-04)
cfg_base = json.load(open(os.path.join(ROOT, "CONFIG", "config.example.yaml").replace(".yaml", ".yaml")))\
    if False else None  # placeholder not used; config is validated via YAML in validate_package.py


# --------------------------------------------------------------- run
def discover_conditionals(schema_dir=None):
    """Every allOf entry with both 'if' and 'then', across every schema in schema_dir
    (default: the package's real SCHEMAS/ directory). A directory parameter is accepted
    so the coverage MECHANISM itself can be exercised against synthetic fixtures in
    TOOLS/test_conditional_coverage.py, without touching the real SCHEMAS/ files."""
    schema_dir = schema_dir or SCH
    found = {}
    for fn in sorted(os.listdir(schema_dir)):
        if not fn.endswith(".schema.json"):
            continue
        doc = json.load(open(os.path.join(schema_dir, fn), encoding="utf-8"))
        entries = []
        for i, item in enumerate(doc.get("allOf", [])):
            if "if" in item and "then" in item:
                entries.append(f"/allOf/{i}")
        if entries:
            found[fn] = entries
    return found


def check_conditional_coverage(case_names_by_result, schema_dir=None, cov_path=None):
    cov_path = cov_path or os.path.join(os.path.dirname(os.path.abspath(__file__)), "conditional_coverage.json")
    if not os.path.exists(cov_path):
        print("FAIL  UNCOVERED CONDITIONAL -- TOOLS/conditional_coverage.json does not exist")
        return False
    coverage = json.load(open(cov_path, encoding="utf-8"))
    discovered = discover_conditionals(schema_dir)

    ok = True
    for schema_file, paths in discovered.items():
        entries_list = coverage.get(schema_file, [])
        # V31.1.2 (item 3): two entries claiming the same conditional path is itself a
        # malformed coverage map (which one governs?) -- flag it explicitly rather than
        # letting the later dict comprehension silently keep only the last one.
        path_counts = collections.Counter(e["path"] for e in entries_list)
        dup_paths = sorted(p for p, n in path_counts.items() if n > 1)
        if dup_paths:
            print(f"FAIL  DUPLICATE COVERAGE ENTRY -- {schema_file} declares {dup_paths} more than "
                  f"once (ambiguous coverage entry)")
            ok = False
        declared = {e["path"]: e for e in entries_list}
        for path in paths:
            if path not in declared:
                print(f"FAIL  UNCOVERED CONDITIONAL -- {schema_file}{path} has no entry in "
                      f"conditional_coverage.json")
                ok = False
                continue
            entry = declared[path]
            # V31.1.2 (item 3): entry.get(...), not entry[...] -- a coverage entry that is
            # missing the key ENTIRELY (not just null) must fail cleanly here, not raise
            # KeyError. Missing-key and explicit-null both resolve to None, and neither
            # None can ever be a case name in case_names_by_result, so both are caught by
            # the same "not in" checks below.
            pos_name, neg_name = entry.get("positive_case"), entry.get("negative_case")
            if pos_name not in case_names_by_result.get(True, set()):
                print(f"FAIL  UNCOVERED CONDITIONAL -- {schema_file}{path}: declared positive_case "
                      f"'{pos_name}' does not exist among passing CASES")
                ok = False
            if neg_name not in case_names_by_result.get(False, set()):
                print(f"FAIL  UNCOVERED CONDITIONAL -- {schema_file}{path}: declared negative_case "
                      f"'{neg_name}' does not exist among failing CASES")
                ok = False

    # also flag stale coverage entries pointing at conditionals that no longer exist
    for schema_file, entries in coverage.items():
        if schema_file.startswith("_"):
            continue
        actual_paths = set(discovered.get(schema_file, []))
        for e in entries:
            if e["path"] not in actual_paths:
                print(f"FAIL  STALE COVERAGE ENTRY -- {schema_file}{e['path']} is declared covered "
                      f"but no longer exists as a schema conditional")
                ok = False
    return ok


def run():
    validators = {}
    for fn in os.listdir(SCH):
        if fn.endswith(".schema.json"):
            name = fn[:-len(".schema.json")]
            schema = json.load(open(os.path.join(SCH, fn), encoding="utf-8"))
            validators[name] = Draft202012Validator(schema, format_checker=FormatChecker())

    passed = failed = 0
    names_by_result = {True: set(), False: set()}
    for schema, name, inst, should_pass, why in CASES:
        v = validators[schema]
        errs = list(v.iter_errors(inst))
        actually_passed = not errs
        ok = actually_passed == should_pass
        names_by_result[should_pass].add(name)
        if ok:
            passed += 1
            print(f"PASS  [{schema}] {name}")
        else:
            failed += 1
            expect = "accept" if should_pass else "REJECT"
            print(f"FAIL  [{schema}] {name} -- expected schema to {expect}. {why}")
            for e in errs[:2]:
                print(f"        {list(e.path)}: {e.message}")

    print("-" * 72)
    neg = sum(1 for c in CASES if not c[3])
    print(f"{passed}/{len(CASES)} conformance cases passed "
          f"({neg} of them are negative cases that must be rejected)")

    print("-" * 72)
    coverage_ok = check_conditional_coverage(names_by_result)
    discovered = discover_conditionals()
    total_conditionals = sum(len(v) for v in discovered.values())
    if coverage_ok:
        print(f"conditional coverage: {total_conditionals}/{total_conditionals} discovered "
              f"if/then conditionals have declared positive+negative coverage (V31-03)")
    else:
        print("conditional coverage: FAILED -- see UNCOVERED CONDITIONAL / STALE COVERAGE ENTRY above")

    return 0 if (failed == 0 and coverage_ok) else 1


if __name__ == "__main__":
    sys.exit(run())
