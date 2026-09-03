#!/usr/bin/env python3
"""
V31.1.1 mutation test suite: for each critical contract, deliberately break it (a
temporary, in-process, in-memory mutation -- never a write to disk, never a change
to a real file) and prove the relevant real check goes from PASS to FAIL. This is
the mission of the whole V3.1.1 hardening pass made executable: "breaking any
critical contract makes the test suite go red."

Every mutation is applied to a scratch copy (a freshly rebuilt schema dict, a
freshly rebuilt transitions list, a monkeypatched module attribute restored in a
`finally`), asserted to flip the real check, and then explicitly proven reverted
before the next mutation runs.
"""
import copy, os, sqlite3, sys
from jsonschema import Draft202012Validator, FormatChecker

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(ROOT, "TOOLS")
sys.path.insert(0, TOOLS)
import generate_artifacts as ga

TS = "2026-09-02T10:00:00+00:00"
RESULTS = []


def record(name, ok, detail=""):
    RESULTS.append((name, ok, detail))
    print(("PASS" if ok else "FAIL") + f"  {name}" + (f"  -- {detail}" if detail and not ok else ""))
    return ok


def validates(schema, instance):
    v = Draft202012Validator(schema, format_checker=FormatChecker())
    return not list(v.iter_errors(instance))


# ===================================================================
# Mutation 1: remove the money precision/non-negativity CHECK (the NONNEG_MONEY
# decimal-string pattern) from a scratch copy of the `job` schema's estimated_cost
# field -> the money precision case ("money with wrong precision") must now
# incorrectly ACCEPT what the real contract rejects.
# ===================================================================

def mutation_1_money_check_removed():
    T = ga.build_transitions()
    sm = ga.build_state_machine_json(T)
    states = [s["name"] for s in sm["states"]]
    schemas = ga.build_schemas(states)
    job_schema = copy.deepcopy(schemas["job"])

    bad_money_instance = {
        "schema_version": ga.SV, "id": "01J8ZQ4T7K9WPX2MNVBCDEFGHJ", "type": "render",
        "state": "QUEUED", "priority": 3, "idempotency_key": "job-render-01J8ZQ4T7K9WPX2MNVBCDEFGHJ",
        "attempt": 0, "max_attempts": 3, "currency": "EUR",
        "correlation_id": "01J8ZQ4T7K9WPX2MNVBCDEFGHJ", "created_at": TS, "updated_at": TS,
        "estimated_cost": "1.50",  # wrong precision (D-023 requires exactly 6 fractional digits)
    }

    real_rejects = not validates(job_schema, bad_money_instance)
    record("mutation1.baseline_real_schema_rejects_wrong_precision_money", real_rejects)

    mutated_schema = copy.deepcopy(job_schema)
    # Remove the pattern-based money constraint entirely -- this is the CHECK
    # equivalent for a JSON-Schema-defined field: without it, any string (or
    # nothing at all constraining the value) is accepted.
    mutated_schema["properties"]["estimated_cost"] = {"oneOf": [{"type": "string"}, {"type": "null"}]}
    mutated_accepts = validates(mutated_schema, bad_money_instance)
    ok = record("mutation1.money_precision_check_goes_red_when_constraint_removed",
                real_rejects and mutated_accepts,
                "removing the money pattern constraint should let wrong-precision money through")

    # Prove reversion: the canonical generator, called fresh, still rejects it.
    fresh_schemas = ga.build_schemas(states)
    still_rejects = not validates(fresh_schemas["job"], bad_money_instance)
    ok = record("mutation1.canonical_generator_unaffected_by_mutation", still_rejects) and ok
    return ok


# ===================================================================
# Mutation 2: allow the non-authoritative evidence source (POST_PUBLISH_CHECK) as
# valid VERIFIED evidence, on a scratch copy of the `publication` schema -> the
# publication/platform evidence case must now incorrectly ACCEPT it.
# ===================================================================

def mutation_2_weak_evidence_allowed_as_verified():
    T = ga.build_transitions()
    sm = ga.build_state_machine_json(T)
    states = [s["name"] for s in sm["states"]]
    schemas = ga.build_schemas(states)
    pub_schema = copy.deepcopy(schemas["publication"])

    base = {"schema_version": ga.SV, "id": "01J8ZQ4T7K9WPX2MNVBCDEFGHJ",
            "production_id": "01J8ZQ4T7K9WPX2MNVBCDEFGHK", "platform": "youtube",
            "account_id": "01J8ZQ4T7K9WPX2MNVBCDEFGHJ", "content_version_id": "01J8ZQ4T7K9WPX2MNVBCDEFGHJ",
            "state": "VERIFIED", "required": True, "platform_label_required": False,
            "idempotency_key": "pub-youtube-01J8ZQ4T7K9WPX2MNVBCDEFGHJ-v1",
            "created_at": TS, "updated_at": TS, "external_id": "abc123",
            "evidence_source": "POST_PUBLISH_CHECK", "evidence_retrieved_at": TS}

    real_rejects = not validates(pub_schema, base)
    record("mutation2.baseline_real_schema_rejects_post_publish_check_as_verified", real_rejects)

    mutated_schema = copy.deepcopy(pub_schema)
    # Widen the VERIFIED conditional's accepted evidence_source enum to include the
    # non-authoritative value -- this is exactly what V31-06 closed; putting it back.
    mutated_schema["allOf"][0]["then"]["properties"]["evidence_source"]["enum"] = \
        ga.AUTHORITATIVE_EVIDENCE + ga.NON_AUTHORITATIVE_EVIDENCE
    mutated_accepts = validates(mutated_schema, base)
    ok = record("mutation2.evidence_check_goes_red_when_weak_evidence_is_allowed",
                real_rejects and mutated_accepts,
                "allowing POST_PUBLISH_CHECK into the VERIFIED conditional should accept it")

    fresh_schemas = ga.build_schemas(states)
    still_rejects = not validates(fresh_schemas["publication"], base)
    ok = record("mutation2.canonical_generator_unaffected_by_mutation", still_rejects) and ok
    return ok


# ===================================================================
# Mutation 3: remove the requirement that synthetic_declaration_id be present when
# a synthetic label is required, on a scratch copy of the `publication` schema ->
# the synthetic disclosure case must now incorrectly ACCEPT a VERIFIED row with a
# required label that has no declaration linked.
# ===================================================================

def mutation_3_synthetic_declaration_requirement_removed():
    T = ga.build_transitions()
    sm = ga.build_state_machine_json(T)
    states = [s["name"] for s in sm["states"]]
    schemas = ga.build_schemas(states)
    pub_schema = copy.deepcopy(schemas["publication"])

    base = {"schema_version": ga.SV, "id": "01J8ZQ4T7K9WPX2MNVBCDEFGHJ",
            "production_id": "01J8ZQ4T7K9WPX2MNVBCDEFGHK", "platform": "youtube",
            "account_id": "01J8ZQ4T7K9WPX2MNVBCDEFGHJ", "content_version_id": "01J8ZQ4T7K9WPX2MNVBCDEFGHJ",
            "state": "VERIFIED", "required": True, "platform_label_required": True,
            "idempotency_key": "pub-youtube-01J8ZQ4T7K9WPX2MNVBCDEFGHJ-v1",
            "created_at": TS, "updated_at": TS, "external_id": "abc123",
            "evidence_source": "OFFICIAL_API", "evidence_retrieved_at": TS,
            "synthetic_label_applied": True}  # no synthetic_declaration_id at all

    real_rejects = not validates(pub_schema, base)
    record("mutation3.baseline_real_schema_rejects_missing_declaration_link", real_rejects)

    mutated_schema = copy.deepcopy(pub_schema)
    # Drop the requirement from the label conditional's "then" clause.
    mutated_schema["allOf"][1]["then"]["required"] = [
        r for r in mutated_schema["allOf"][1]["then"]["required"] if r != "synthetic_declaration_id"
    ]
    mutated_accepts = validates(mutated_schema, base)
    ok = record("mutation3.synthetic_disclosure_check_goes_red_when_requirement_removed",
                real_rejects and mutated_accepts,
                "removing the synthetic_declaration_id requirement should accept an unlinked label")

    fresh_schemas = ga.build_schemas(states)
    still_rejects = not validates(fresh_schemas["publication"], base)
    ok = record("mutation3.canonical_generator_unaffected_by_mutation", still_rejects) and ok

    # Same invariant, at the database layer: also prove it on the canonical DDL,
    # since that is the structural (not merely schema-level) enforcement.
    ddl = ga.build_canonical_ddl()
    conn = sqlite3.connect(":memory:")
    conn.executescript(ddl["synthetic_declarations"])
    conn.executescript(ddl["publications"])
    try:
        conn.execute(
            "INSERT INTO publications (id, state, external_id, evidence_source, "
            "evidence_retrieved_at, synthetic_declaration_id, platform_label_required, "
            "synthetic_label_applied) VALUES (?,?,?,?,?,?,?,?)",
            ("p-mut3", "VERIFIED", "abc123", "OFFICIAL_API", TS, None, 1, 1))
        conn.commit()
        db_rejects = False
    except sqlite3.IntegrityError:
        db_rejects = True
    conn.close()
    ok = record("mutation3.database_layer_also_rejects_missing_declaration_link_unmutated", db_rejects) and ok
    return ok


# ===================================================================
# Mutation 4: allow a state machine transition directly from a pre-verification
# state to a verified state, skipping the required intermediate verification step
# (e.g. INIT -> RESEARCH_VERIFIED, bypassing RESEARCHING) -> validate_state_machine
# must now report an error where the unmutated model reports none.
# ===================================================================

def mutation_4_verification_skipping_transition():
    T = ga.build_transitions()
    baseline_errs = ga.validate_state_machine(T)
    ok = record("mutation4.baseline_real_transitions_have_no_verification_skip", not baseline_errs,
                "; ".join(baseline_errs[:3]))

    mutated_T = copy.deepcopy(T)
    mutated_T.append(dict(id="T-MUTATION-4", **{"from": "INIT"}, to="RESEARCH_VERIFIED",
                          trigger="shortcut_claimed_verified", guard="(illegal test mutation)",
                          actor="Orchestrator"))
    mutated_errs = ga.validate_state_machine(mutated_T)
    flagged = any("RESEARCH_VERIFIED" in e and "verification-skipping" in e for e in mutated_errs)
    ok = record("mutation4.state_machine_check_goes_red_on_verification_skip", flagged,
                "; ".join(mutated_errs[:3])) and ok

    fresh_T = ga.build_transitions()
    still_clean = not ga.validate_state_machine(fresh_T)
    ok = record("mutation4.canonical_generator_unaffected_by_mutation", still_clean) and ok
    return ok


# ===================================================================
# Mutation 5: introduce a float() cast into a money-handling code path in
# generate_artifacts.py's in-memory source, then revert -- prove the D-023
# no-float-in-tooling AST guard (used by both test_money_precision.py and
# validate_package.py) fails against the mutated source text, never touching the
# real file on disk.
# ===================================================================

def mutation_5_float_introduced_in_money_path():
    import ast
    real_path = os.path.join(TOOLS, "generate_artifacts.py")
    with open(real_path, encoding="utf-8") as f:
        real_src = f.read()

    def offenders(src, filename):
        tree = ast.parse(src, filename=filename)
        return [f"{filename}:{n.lineno}" for n in ast.walk(tree)
                if isinstance(n, ast.Call) and isinstance(n.func, ast.Name) and n.func.id == "float"]

    real_offenders = offenders(real_src, "generate_artifacts.py")
    ok = record("mutation5.baseline_real_source_has_no_float_calls", not real_offenders, str(real_offenders))

    # Inject `float(...)` into a money-adjacent line, in memory only.
    anchor = 'NONNEG_MONEY = {\n'
    assert anchor in real_src, "expected anchor not found in generate_artifacts.py"
    mutated_src = real_src.replace(
        anchor,
        'MUTATION_TEST_MONEY_AS_FLOAT = float("1.500000")  # deliberately introduced by test_mutations.py\n'
        + anchor,
        1,
    )
    assert mutated_src != real_src

    mutated_offenders = offenders(mutated_src, "generate_artifacts.py")
    ok = record("mutation5.no_float_guard_goes_red_when_float_call_introduced",
                bool(mutated_offenders), f"expected at least one offender, got {mutated_offenders}") and ok

    # Prove the real file on disk was never touched.
    with open(real_path, encoding="utf-8") as f:
        disk_src_after = f.read()
    ok = record("mutation5.real_file_on_disk_untouched", disk_src_after == real_src) and ok
    return ok


# ===================================================================
# Mutation 6: add a conditional (if/then) to a synthetic schema fixture with no
# coverage entry -> the conditional coverage check must FAIL. Reuses the Phase 3
# mechanism harness (conformance_tests.check_conditional_coverage against a
# scratch schema_dir / cov_path).
# ===================================================================

def mutation_6_uncovered_conditional():
    import json, tempfile, shutil
    import conformance_tests as ct

    schema_dir = tempfile.mkdtemp(prefix="amcca_mut6_schema_")
    doc = {"type": "object",
           "allOf": [{"if": {"properties": {"x": {"const": "y"}}}, "then": {"required": ["z"]}}]}
    with open(os.path.join(schema_dir, "scratch.schema.json"), "w", encoding="utf-8") as f:
        json.dump(doc, f)
    fd, empty_cov_path = tempfile.mkstemp(prefix="amcca_mut6_cov_", suffix=".json")
    with open(fd, "w", encoding="utf-8") as f:
        json.dump({}, f)  # no coverage entry at all for the injected conditional

    try:
        ok_check = ct.check_conditional_coverage({True: set(), False: set()},
                                                  schema_dir=schema_dir, cov_path=empty_cov_path)
        ok = record("mutation6.conditional_coverage_check_goes_red_on_uncovered_conditional",
                    ok_check is False)
    finally:
        shutil.rmtree(schema_dir, ignore_errors=True)
        os.remove(empty_cov_path)

    # Prove reversion: the real package is still fully covered.
    real_ok = ct.check_conditional_coverage({True: {n for _, n, _, sp, _ in ct.CASES if sp},
                                             False: {n for _, n, _, sp, _ in ct.CASES if not sp}})
    ok = record("mutation6.real_package_still_fully_covered", real_ok) and ok
    return ok


# ===================================================================
# Mutation 7 (V3.1.2 item 5): introduce a transition FROM a terminal state
# (ARCHIVED) -> validate_state_machine's "terminal has outbound" structural
# guarantee must FAIL where the unmutated model reports nothing.
# ===================================================================

def mutation_7_transition_from_terminal_state():
    T = ga.build_transitions()
    baseline_errs = ga.validate_state_machine(T)
    ok = record("mutation7.baseline_real_transitions_have_no_terminal_outbound", not baseline_errs,
                "; ".join(baseline_errs[:3]))

    mutated_T = copy.deepcopy(T)
    mutated_T.append(dict(id="T-MUTATION-7", **{"from": "ARCHIVED"}, to="RESEARCHING",
                          trigger="illegal_resurrection", guard="(illegal test mutation)",
                          actor="Orchestrator"))
    mutated_errs = ga.validate_state_machine(mutated_T)
    flagged = any("terminal has outbound: ARCHIVED" in e for e in mutated_errs)
    ok = record("mutation7.state_machine_check_goes_red_on_transition_from_terminal", flagged,
                "; ".join(mutated_errs[:3])) and ok

    fresh_T = ga.build_transitions()
    still_clean = not ga.validate_state_machine(fresh_T)
    ok = record("mutation7.canonical_generator_unaffected_by_mutation", still_clean) and ok
    return ok


# ===================================================================
# Mutation 8 (V3.1.2 item 5): introduce a transition TO a state name that does not
# exist in ALL_STATES -> validate_state_machine's "unknown to" check must FAIL.
# ===================================================================

def mutation_8_transition_to_nonexistent_state():
    T = ga.build_transitions()
    baseline_errs = ga.validate_state_machine(T)
    ok = record("mutation8.baseline_real_transitions_have_no_unknown_state", not baseline_errs,
                "; ".join(baseline_errs[:3]))

    mutated_T = copy.deepcopy(T)
    mutated_T.append(dict(id="T-MUTATION-8", **{"from": "INIT"}, to="NO_SUCH_STATE",
                          trigger="typo_target", guard="(illegal test mutation)",
                          actor="Orchestrator"))
    mutated_errs = ga.validate_state_machine(mutated_T)
    flagged = any("unknown to NO_SUCH_STATE" in e for e in mutated_errs)
    ok = record("mutation8.state_machine_check_goes_red_on_transition_to_nonexistent_state", flagged,
                "; ".join(mutated_errs[:3])) and ok

    fresh_T = ga.build_transitions()
    still_clean = not ga.validate_state_machine(fresh_T)
    ok = record("mutation8.canonical_generator_unaffected_by_mutation", still_clean) and ok
    return ok


# ===================================================================
# Mutation 9 (V3.1.2 item 5): duplicate a transition ID -> validate_state_machine's
# "duplicate transition ids" check must FAIL.
# ===================================================================

def mutation_9_duplicate_transition_id():
    T = ga.build_transitions()
    baseline_errs = ga.validate_state_machine(T)
    ok = record("mutation9.baseline_real_transitions_have_no_duplicate_ids", not baseline_errs,
                "; ".join(baseline_errs[:3]))

    mutated_T = copy.deepcopy(T)
    assert mutated_T[0]["id"] != mutated_T[1]["id"]
    mutated_T[1] = dict(mutated_T[1], id=mutated_T[0]["id"])
    mutated_errs = ga.validate_state_machine(mutated_T)
    flagged = any("duplicate transition ids" in e and mutated_T[0]["id"] in e for e in mutated_errs)
    ok = record("mutation9.state_machine_check_goes_red_on_duplicate_transition_id", flagged,
                "; ".join(mutated_errs[:3])) and ok

    fresh_T = ga.build_transitions()
    still_clean = not ga.validate_state_machine(fresh_T)
    ok = record("mutation9.canonical_generator_unaffected_by_mutation", still_clean) and ok
    return ok


# ===================================================================
# Mutation 10 (V3.1.2 item 5): remove the requirement that evidence_retrieved_at be
# present for a VERIFIED publication -> the I-11 conditional must now incorrectly
# ACCEPT a VERIFIED row that never says when its evidence was retrieved.
# ===================================================================

def mutation_10_evidence_retrieved_at_requirement_removed():
    T = ga.build_transitions()
    sm = ga.build_state_machine_json(T)
    states = [s["name"] for s in sm["states"]]
    schemas = ga.build_schemas(states)
    pub_schema = copy.deepcopy(schemas["publication"])

    base = {"schema_version": ga.SV, "id": "01J8ZQ4T7K9WPX2MNVBCDEFGHJ",
            "production_id": "01J8ZQ4T7K9WPX2MNVBCDEFGHK", "platform": "youtube",
            "account_id": "01J8ZQ4T7K9WPX2MNVBCDEFGHJ", "content_version_id": "01J8ZQ4T7K9WPX2MNVBCDEFGHJ",
            "state": "VERIFIED", "required": True, "platform_label_required": False,
            "idempotency_key": "pub-youtube-01J8ZQ4T7K9WPX2MNVBCDEFGHJ-v1",
            "created_at": TS, "updated_at": TS, "external_id": "abc123",
            "evidence_source": "OFFICIAL_API"}  # no evidence_retrieved_at at all

    real_rejects = not validates(pub_schema, base)
    record("mutation10.baseline_real_schema_rejects_verified_without_evidence_retrieved_at", real_rejects)

    mutated_schema = copy.deepcopy(pub_schema)
    mutated_schema["allOf"][0]["then"]["required"] = [
        r for r in mutated_schema["allOf"][0]["then"]["required"] if r != "evidence_retrieved_at"
    ]
    mutated_accepts = validates(mutated_schema, base)
    ok = record("mutation10.I11_check_goes_red_when_evidence_retrieved_at_requirement_removed",
                real_rejects and mutated_accepts,
                "removing the evidence_retrieved_at requirement should accept a VERIFIED row missing it")

    fresh_schemas = ga.build_schemas(states)
    still_rejects = not validates(fresh_schemas["publication"], base)
    ok = record("mutation10.canonical_generator_unaffected_by_mutation", still_rejects) and ok
    return ok


# ===================================================================
# Mutation 11 (V3.1.2 item 5): remove the requirement that external_id be present
# for a VERIFIED publication -> the I-11 conditional must now incorrectly ACCEPT a
# VERIFIED row with no external identifier at all.
# ===================================================================

def mutation_11_external_id_requirement_removed():
    T = ga.build_transitions()
    sm = ga.build_state_machine_json(T)
    states = [s["name"] for s in sm["states"]]
    schemas = ga.build_schemas(states)
    pub_schema = copy.deepcopy(schemas["publication"])

    base = {"schema_version": ga.SV, "id": "01J8ZQ4T7K9WPX2MNVBCDEFGHJ",
            "production_id": "01J8ZQ4T7K9WPX2MNVBCDEFGHK", "platform": "youtube",
            "account_id": "01J8ZQ4T7K9WPX2MNVBCDEFGHJ", "content_version_id": "01J8ZQ4T7K9WPX2MNVBCDEFGHJ",
            "state": "VERIFIED", "required": True, "platform_label_required": False,
            "idempotency_key": "pub-youtube-01J8ZQ4T7K9WPX2MNVBCDEFGHJ-v1",
            "created_at": TS, "updated_at": TS,
            "evidence_source": "OFFICIAL_API", "evidence_retrieved_at": TS}  # no external_id at all

    real_rejects = not validates(pub_schema, base)
    record("mutation11.baseline_real_schema_rejects_verified_without_external_id", real_rejects)

    mutated_schema = copy.deepcopy(pub_schema)
    mutated_schema["allOf"][0]["then"]["required"] = [
        r for r in mutated_schema["allOf"][0]["then"]["required"] if r != "external_id"
    ]
    mutated_accepts = validates(mutated_schema, base)
    ok = record("mutation11.I11_check_goes_red_when_external_id_requirement_removed",
                real_rejects and mutated_accepts,
                "removing the external_id requirement should accept a VERIFIED row missing it")

    fresh_schemas = ga.build_schemas(states)
    still_rejects = not validates(fresh_schemas["publication"], base)
    ok = record("mutation11.canonical_generator_unaffected_by_mutation", still_rejects) and ok
    return ok


# ===================================================================
# Mutation 12 (V3.1.2 item 5): type a money field as JSON Schema `number` instead of
# string+decimal-pattern -> the generated-artifact semantic check that asserts no
# money field is typed `number` (test_generated_artifacts_semantics.py's
# invariant.no_money_field_typed_as_number) must FAIL. Reimplements that exact
# detection logic inline (same heuristic, same field-name allowlist) so this
# mutation exercises the real invariant rather than a fresh one invented here.
# ===================================================================

def _money_fields_typed_as_number(schemas):
    bad_money = []
    for name, doc in schemas.items():
        for pname, pdef in doc.get("properties", {}).items():
            if pname not in ("amount", "estimated_cost", "reserved_cost", "limit_amount"):
                continue
            defs = pdef.get("oneOf", [pdef])
            for d in defs:
                if isinstance(d, dict) and d.get("type") == "number":
                    bad_money.append(f"{name}.{pname}")
    return bad_money


def mutation_12_money_field_typed_as_number():
    T = ga.build_transitions()
    sm = ga.build_state_machine_json(T)
    states = [s["name"] for s in sm["states"]]
    schemas = ga.build_schemas(states)

    real_bad = _money_fields_typed_as_number(schemas)
    ok = record("mutation12.baseline_real_schemas_have_no_money_field_typed_as_number", not real_bad, str(real_bad))

    mutated_schemas = copy.deepcopy(schemas)
    # cost-event.amount is normally SignedMoney (string + decimal pattern); retype it
    # as a bare JSON Schema number, exactly the D-023 violation this invariant exists
    # to catch.
    mutated_schemas["cost-event"]["properties"]["amount"] = {"type": "number"}
    mutated_bad = _money_fields_typed_as_number(mutated_schemas)
    ok = record("mutation12.no_money_as_number_invariant_goes_red_when_field_retyped",
                not real_bad and "cost-event.amount" in mutated_bad, str(mutated_bad)) and ok

    fresh_schemas = ga.build_schemas(states)
    still_clean = not _money_fields_typed_as_number(fresh_schemas)
    ok = record("mutation12.canonical_generator_unaffected_by_mutation", still_clean) and ok
    return ok


# ===================================================================
# Mutation 13 (V3.1.2 item 5): widen a NonNegativeMoney pattern to accept a leading
# minus sign (i.e. swap it for the SignedMoney shape) -> the "negative estimated
# cost" conformance case (V31-04) must now incorrectly ACCEPT a negative value.
# ===================================================================

def mutation_13_nonnegative_money_accepts_negative():
    T = ga.build_transitions()
    sm = ga.build_state_machine_json(T)
    states = [s["name"] for s in sm["states"]]
    schemas = ga.build_schemas(states)
    job_schema = copy.deepcopy(schemas["job"])

    negative_cost_instance = {
        "schema_version": ga.SV, "id": "01J8ZQ4T7K9WPX2MNVBCDEFGHJ", "type": "render",
        "state": "QUEUED", "priority": 3, "idempotency_key": "job-render-01J8ZQ4T7K9WPX2MNVBCDEFGHJ",
        "attempt": 0, "max_attempts": 3, "currency": "EUR",
        "correlation_id": "01J8ZQ4T7K9WPX2MNVBCDEFGHJ", "created_at": TS, "updated_at": TS,
        "estimated_cost": "-1.000000",  # V31-04: estimated_cost is NonNegativeMoney
    }

    real_rejects = not validates(job_schema, negative_cost_instance)
    record("mutation13.baseline_real_schema_rejects_negative_nonnegative_money", real_rejects)

    mutated_schema = copy.deepcopy(job_schema)
    # Swap the NonNegativeMoney pattern for the SignedMoney pattern (ga.SIGNED_MONEY)
    # inside the oneOf branch that carries the money constraint.
    branches = mutated_schema["properties"]["estimated_cost"]["oneOf"]
    for i, b in enumerate(branches):
        if isinstance(b, dict) and b.get("type") == "string" and "pattern" in b:
            branches[i] = dict(b, pattern=ga.SIGNED_MONEY["pattern"])
    mutated_accepts = validates(mutated_schema, negative_cost_instance)
    ok = record("mutation13.money_nonnegativity_check_goes_red_when_pattern_widened",
                real_rejects and mutated_accepts,
                "widening estimated_cost's pattern to SignedMoney should accept a negative value")

    fresh_schemas = ga.build_schemas(states)
    still_rejects = not validates(fresh_schemas["job"], negative_cost_instance)
    ok = record("mutation13.canonical_generator_unaffected_by_mutation", still_rejects) and ok
    return ok


# ===================================================================
# Mutation 14 (V3.1.2 item 5): inject a reference to a nonexistent decision ID
# (D-999, which DECISIONS.md does not declare) into a scratch copy of the package
# tree -> validate_package.py's real refs.all_decision_ids_exist check must FAIL.
# Exercises the actual check function (not a reimplementation of its set logic) by
# monkeypatching validate_package.ROOT/.RESULTS to point at a throwaway copy of
# SPEC/BLUEPRINT/SCHEMAS/POLICIES/CONFIG/DECISIONS.md; the real repository is never
# touched, and the monkeypatch is always reverted in a `finally`.
# ===================================================================

def mutation_14_reference_to_nonexistent_decision_id():
    import shutil, tempfile
    import validate_package as vp

    assert "D-999" not in read_decisions_declared_and_check(), \
        "test fixture assumption violated: D-999 must not be a real declared decision id"

    scratch = tempfile.mkdtemp(prefix="amcca_mut14_root_")
    real_root, real_results = vp.ROOT, vp.RESULTS
    try:
        for name in ("SPEC", "BLUEPRINT", "SCHEMAS", "POLICIES", "CONFIG", "DECISIONS.md"):
            src = os.path.join(ROOT, name)
            dst = os.path.join(scratch, name)
            if os.path.isdir(src):
                shutil.copytree(src, dst)
            elif os.path.isfile(src):
                shutil.copy2(src, dst)

        vp.ROOT = scratch
        vp.RESULTS = []
        vp.check_references()
        baseline = {r["check"]: r for r in vp.RESULTS}
        baseline_ok = baseline["refs.all_decision_ids_exist"]["ok"]
        ok = record("mutation14.baseline_scratch_copy_has_no_undefined_decision_ids", baseline_ok)

        spec_dir = os.path.join(scratch, "SPEC")
        target_file = sorted(f for f in os.listdir(spec_dir) if f.endswith(".md"))[0]
        with open(os.path.join(spec_dir, target_file), "a", encoding="utf-8") as f:
            f.write("\n\nSee D-999 for rationale (deliberately nonexistent; test_mutations.py).\n")

        vp.RESULTS = []
        vp.check_references()
        mutated = {r["check"]: r for r in vp.RESULTS}
        mutated_ok = mutated["refs.all_decision_ids_exist"]["ok"]
        ok = record("mutation14.decision_reference_check_goes_red_on_nonexistent_decision_id",
                    baseline_ok and not mutated_ok,
                    mutated["refs.all_decision_ids_exist"]["detail"]) and ok
    finally:
        vp.ROOT, vp.RESULTS = real_root, real_results
        shutil.rmtree(scratch, ignore_errors=True)

    # Prove reversion: the real package tree, checked directly, still has no
    # undefined decision ids.
    saved = vp.RESULTS
    vp.RESULTS = []
    vp.check_references()
    real_check = {r["check"]: r for r in vp.RESULTS}
    still_ok = real_check["refs.all_decision_ids_exist"]["ok"]
    vp.RESULTS = saved
    ok = record("mutation14.real_package_unaffected_by_mutation", still_ok) and ok
    return ok


def read_decisions_declared_and_check():
    import re
    dec_path = os.path.join(ROOT, "DECISIONS.md")
    with open(dec_path, encoding="utf-8") as f:
        dec = f.read()
    return set(re.findall(r"^### (D-\d{3})", dec, re.M))


def run():
    mutations = [
        mutation_1_money_check_removed,
        mutation_2_weak_evidence_allowed_as_verified,
        mutation_3_synthetic_declaration_requirement_removed,
        mutation_4_verification_skipping_transition,
        mutation_5_float_introduced_in_money_path,
        mutation_6_uncovered_conditional,
        mutation_7_transition_from_terminal_state,
        mutation_8_transition_to_nonexistent_state,
        mutation_9_duplicate_transition_id,
        mutation_10_evidence_retrieved_at_requirement_removed,
        mutation_11_external_id_requirement_removed,
        mutation_12_money_field_typed_as_number,
        mutation_13_nonnegative_money_accepts_negative,
        mutation_14_reference_to_nonexistent_decision_id,
    ]
    results = []
    for m in mutations:
        print(f"\n-- {m.__name__} " + "-" * (60 - len(m.__name__)))
        results.append(m())

    print("\n" + "-" * 72)
    passed = sum(1 for r in results if r)
    print(f"{passed}/{len(results)} mutation tests demonstrated a red flip "
          f"(break the contract -> the relevant check fails)")
    return 0 if all(results) else 1


if __name__ == "__main__":
    sys.exit(run())
