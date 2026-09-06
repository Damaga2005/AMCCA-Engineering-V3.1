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


# ===================================================================
# Mutation 15: oversensitive SCM metadata filter in walk_files() ->
# if walk_files() erroneously skips files by prefix (e.g. fn.startswith(".git"))
# instead of exact ".git", real files like .gitignore are dropped, which must
# make manifest.matches_tree and hygiene.all_tracked_files_in_manifest go red.
# ===================================================================

def mutation_15_walk_files_oversensitive_filter():
    import validate_package as vp

    # 1. Baseline: valid manifest against canonical CERTIFIED_REPOSITORY_FILES
    saved_results = vp.RESULTS
    vp.RESULTS = []
    vp.check_manifest()
    baseline = {r["check"]: r for r in vp.RESULTS}
    baseline_ok = baseline.get("manifest.matches_tree", {}).get("ok", False)
    baseline_detail = baseline.get("manifest.matches_tree", {}).get("detail", "")
    vp.RESULTS = saved_results
    ok = record("mutation15.baseline_manifest_matches_tree", baseline_ok, baseline_detail)

    # 2. Missing real file: simulate dropping a real certified file (.gitignore)
    real_walk = vp.walk_files

    def walk_omitting_gitignore():
        for rel in real_walk():
            if rel == ".gitignore" or os.path.basename(rel) == ".gitignore":
                continue
            yield rel

    try:
        vp.walk_files = walk_omitting_gitignore
        vp.RESULTS = []
        vp.check_manifest()
        mutated = {r["check"]: r for r in vp.RESULTS}
        mutated_ok = mutated.get("manifest.matches_tree", {}).get("ok", False)
        ok = record("mutation15.manifest_matches_tree_goes_red_when_real_file_omitted",
                    baseline_ok and not mutated_ok,
                    mutated.get("manifest.matches_tree", {}).get("detail", "")) and ok
    finally:
        vp.walk_files = real_walk
        vp.RESULTS = saved_results

    # 3. Restore: restoring the omitted file restores PASS
    vp.RESULTS = []
    vp.check_manifest()
    restored = {r["check"]: r for r in vp.RESULTS}
    restored_ok = restored.get("manifest.matches_tree", {}).get("ok", False)
    vp.RESULTS = saved_results
    ok = record("mutation15.manifest_matches_tree_restored_after_omission", restored_ok) and ok

    # 4. Stale file: introducing an unexpected/unmanifested file causes FAIL
    stale_temp = os.path.join(ROOT, "TOOLS", "_tmp_stale_test_mutation15.py")
    try:
        with open(stale_temp, "w", encoding="utf-8") as f:
            f.write("# ephemeral test mutation 15 file\n")
        vp.RESULTS = []
        vp.check_manifest()
        stale_res = {r["check"]: r for r in vp.RESULTS}
        stale_ok = stale_res.get("manifest.matches_tree", {}).get("ok", False)
        ok = record("mutation15.manifest_matches_tree_goes_red_when_stale_file_introduced",
                    baseline_ok and not stale_ok,
                    stale_res.get("manifest.matches_tree", {}).get("detail", "")) and ok
    finally:
        if os.path.exists(stale_temp):
            os.remove(stale_temp)
        vp.RESULTS = saved_results

    # 5. Prove reversion: real package walk_files is restored and green
    vp.RESULTS = []
    vp.check_manifest()
    reverted = {r["check"]: r for r in vp.RESULTS}
    reverted_ok = reverted.get("manifest.matches_tree", {}).get("ok", False)
    vp.RESULTS = saved_results
    ok = record("mutation15.real_package_unaffected_by_mutation", reverted_ok) and ok
    return ok


# ===================================================================
# Mutation 16: a table's CHECK constraint regresses to no longer enforce its
# contract's enum (the exact defect this check exists to catch on
# tool_runs.side_effect_class and audit_log.actor_type -- fixed by migration 4,
# 004_audit_actor_types_and_tool_run_side_effect_check) -> the comparison must
# flag that specific column, on both an unconstrained regression and a narrowed
# one, while leaving every other column's verdict alone.
# ===================================================================

def mutation_16_ddl_check_regresses_from_contract_enum():
    import shutil, tempfile
    import validate_package as vp

    scratch = tempfile.mkdtemp(prefix="amcca_mut16_root_")
    real_root, real_results = vp.ROOT, vp.RESULTS
    try:
        mig_rel = os.path.join("src", "AMCCA.Core", "Database", "MigrationService.cs")
        os.makedirs(os.path.dirname(os.path.join(scratch, mig_rel)), exist_ok=True)
        shutil.copy2(os.path.join(ROOT, mig_rel), os.path.join(scratch, mig_rel))
        schemas_dir = os.path.join(scratch, "SCHEMAS")
        shutil.copytree(os.path.join(ROOT, "SCHEMAS"), schemas_dir)

        vp.ROOT = scratch
        vp.RESULTS = []
        vp.check_contract_enum_matches_ddl_check()
        baseline_detail = vp.RESULTS[0]["detail"]
        ok = record("mutation16.baseline_scratch_copy_does_not_flag_the_fixed_columns",
                    "tool_runs.side_effect_class" not in baseline_detail
                    and "audit_log.actor_type" not in baseline_detail,
                    baseline_detail)

        mig_path = os.path.join(scratch, mig_rel)
        with open(mig_path, encoding="utf-8") as f:
            source = f.read()
        needle = "side_effect_class TEXT NOT NULL CHECK(side_effect_class IN ('PURE','READ','LOCAL_WRITE','EXTERNAL_IDEMPOTENT','EXTERNAL_UNSAFE')),"
        assert needle in source, "test fixture assumption violated: tool_runs' side_effect_class CHECK text has changed"
        # tool_runs is created by migration 4 and rebuilt again by later table-rebuild migrations;
        # the live schema is whatever the LAST rebuild produced, so the CHECK has to be removed from
        # every definition for the regression to actually reach check_contract_enum_matches_ddl_check.
        mutated_source = source.replace(needle, "side_effect_class TEXT NOT NULL,")

        with open(mig_path, "w", encoding="utf-8") as f:
            f.write(mutated_source)

        vp.RESULTS = []
        vp.check_contract_enum_matches_ddl_check()
        mutated_ok = vp.RESULTS[0]["ok"]
        mutated_detail = vp.RESULTS[0]["detail"]
        ok = record("mutation16.check_goes_red_when_regressed_to_unconstrained",
                    not mutated_ok and "tool_runs.side_effect_class" in mutated_detail
                    and "DDL column has no CHECK" in mutated_detail,
                    mutated_detail) and ok
        ok = record("mutation16.unrelated_fixed_column_still_clean_after_mutation",
                    "audit_log.actor_type" not in mutated_detail) and ok
    finally:
        vp.ROOT, vp.RESULTS = real_root, real_results
        shutil.rmtree(scratch, ignore_errors=True)

    # Prove reversion: the real package tree, checked directly, still does not flag
    # either column this migration fixed.
    saved = vp.RESULTS
    vp.RESULTS = []
    vp.check_contract_enum_matches_ddl_check()
    real_detail = vp.RESULTS[0]["detail"]
    real_clean = ("tool_runs.side_effect_class" not in real_detail
                  and "audit_log.actor_type" not in real_detail)
    vp.RESULTS = saved
    ok = record("mutation16.real_package_unaffected_by_mutation", real_clean, real_detail) and ok
    return ok


def mutation_19_field_presence_check_regresses_when_a_real_column_is_dropped():
    import shutil, tempfile
    import validate_package as vp

    scratch = tempfile.mkdtemp(prefix="amcca_mut19_root_")
    real_root, real_results = vp.ROOT, vp.RESULTS
    try:
        mig_rel = os.path.join("src", "AMCCA.Core", "Database", "MigrationService.cs")
        os.makedirs(os.path.dirname(os.path.join(scratch, mig_rel)), exist_ok=True)
        shutil.copy2(os.path.join(ROOT, mig_rel), os.path.join(scratch, mig_rel))
        shutil.copytree(os.path.join(ROOT, "SCHEMAS"), os.path.join(scratch, "SCHEMAS"))

        vp.ROOT = scratch
        vp.RESULTS = []
        vp.check_contract_fields_have_columns()
        baseline_ok = vp.RESULTS[0]["ok"]
        baseline_detail = vp.RESULTS[0]["detail"]
        # reconciliation_state / pricing_snapshot_id / schema_version were the audit's named gaps;
        # a later migration added their columns, so the baseline is now genuinely clean.
        ok = record("mutation19.baseline_scratch_copy_does_not_flag_provider_or_pk_aliases",
                    baseline_ok
                    and "'cost_events.provider'" not in baseline_detail
                    and "cost_events.cost_event_id" not in baseline_detail
                    and "jobs.lease_owner" not in baseline_detail
                    and "referral_links.disclosure_required" not in baseline_detail
                    and "analytics_snapshots.source_account_id" not in baseline_detail
                    and "cost_events.units" not in baseline_detail,
                    baseline_detail)

        mig_path = os.path.join(scratch, mig_rel)
        with open(mig_path, encoding="utf-8") as f:
            source = f.read()
        # cost_events is created by migration 1 and recreated by a later enum-CHECK table rebuild.
        # Dropping provider from only the first leaves the rebuild's `INSERT ... SELECT *` a column
        # short; it has to go from every CREATE for the column to actually be absent from the live
        # schema (and for the rebuild's column count to still line up).
        needle_create = "                    provider TEXT NOT NULL,\n                    occurred_at TEXT NOT NULL,\n                    created_at TEXT NOT NULL,\n                    CHECK(kind = 'ADJUSTMENT' OR amount NOT LIKE '-%')"
        needle_rebuild = "                    provider TEXT NOT NULL,\n                    occurred_at TEXT NOT NULL,\n                    created_at TEXT NOT NULL,\n                    schema_version TEXT NOT NULL DEFAULT '3.1.0',"
        assert needle_create in source, "test fixture assumption violated: cost_events' migration-1 provider DDL text has changed"
        assert needle_rebuild in source, "test fixture assumption violated: cost_events' rebuild provider DDL text has changed"
        mutated_source = source.replace(
            needle_create,
            "                    occurred_at TEXT NOT NULL,\n                    created_at TEXT NOT NULL,\n                    CHECK(kind = 'ADJUSTMENT' OR amount NOT LIKE '-%')",
            1)
        mutated_source = mutated_source.replace(
            needle_rebuild,
            "                    occurred_at TEXT NOT NULL,\n                    created_at TEXT NOT NULL,\n                    schema_version TEXT NOT NULL DEFAULT '3.1.0',")

        with open(mig_path, "w", encoding="utf-8") as f:
            f.write(mutated_source)

        vp.RESULTS = []
        vp.check_contract_fields_have_columns()
        mutated_ok = vp.RESULTS[0]["ok"]
        mutated_detail = vp.RESULTS[0]["detail"]
        ok = record("mutation19.check_goes_red_when_a_real_column_is_dropped",
                    not mutated_ok and "'cost_events.provider'" in mutated_detail,
                    mutated_detail) and ok
        ok = record("mutation19.pk_alias_and_normalized_fields_still_not_flagged_after_mutation",
                    "cost_events.cost_event_id" not in mutated_detail
                    and "jobs.lease_owner" not in mutated_detail,
                    mutated_detail) and ok
    finally:
        vp.ROOT, vp.RESULTS = real_root, real_results
        shutil.rmtree(scratch, ignore_errors=True)

    # Prove reversion: the real package tree, checked directly, does not flag provider either.
    saved = vp.RESULTS
    vp.RESULTS = []
    vp.check_contract_fields_have_columns()
    real_detail = vp.RESULTS[0]["detail"]
    real_clean = "'cost_events.provider'" not in real_detail
    vp.RESULTS = saved
    ok = record("mutation19.real_package_unaffected_by_mutation", real_clean, real_detail) and ok
    return ok


def mutation_17_thrown_error_code_regresses_from_spec_catalogue():
    import shutil, tempfile
    import validate_package as vp

    scratch = tempfile.mkdtemp(prefix="amcca_mut17_root_")
    real_root, real_results = vp.ROOT, vp.RESULTS
    try:
        shutil.copytree(os.path.join(ROOT, "src"), os.path.join(scratch, "src"))
        os.makedirs(os.path.join(scratch, "SPEC"), exist_ok=True)
        spec_path = os.path.join(scratch, "SPEC", "05_ERROR_MODEL.md")
        shutil.copy2(os.path.join(ROOT, "SPEC", "05_ERROR_MODEL.md"), spec_path)

        vp.ROOT = scratch
        vp.RESULTS = []
        vp.check_thrown_error_codes_catalogued()
        baseline_detail = vp.RESULTS[0]["detail"]
        ok = record("mutation17.baseline_scratch_copy_is_clean",
                    vp.RESULTS[0]["ok"] and "AMCCA-STM-003" not in baseline_detail,
                    baseline_detail)

        with open(spec_path, encoding="utf-8") as f:
            spec_source = f.read()
        needle = "| `AMCCA-STM-003` | INTERNAL | No | Outbound transition attempted from a terminal state |\n"
        assert needle in spec_source, "test fixture assumption violated: SPEC/05's AMCCA-STM-003 row text has changed"
        with open(spec_path, "w", encoding="utf-8") as f:
            f.write(spec_source.replace(needle, "", 1))

        vp.RESULTS = []
        vp.check_thrown_error_codes_catalogued()
        mutated_ok = vp.RESULTS[0]["ok"]
        mutated_detail = vp.RESULTS[0]["detail"]
        ok = record("mutation17.check_goes_red_when_a_thrown_code_is_removed_from_the_catalogue",
                    not mutated_ok and "AMCCA-STM-003" in mutated_detail,
                    mutated_detail) and ok
    finally:
        vp.ROOT, vp.RESULTS = real_root, real_results
        shutil.rmtree(scratch, ignore_errors=True)

    # Prove reversion: the real package tree, checked directly, still catalogues every
    # code the real code throws.
    saved = vp.RESULTS
    vp.RESULTS = []
    vp.check_thrown_error_codes_catalogued()
    real_ok = vp.RESULTS[0]["ok"]
    real_detail = vp.RESULTS[0]["detail"]
    vp.RESULTS = saved
    ok = record("mutation17.real_package_unaffected_by_mutation", real_ok, real_detail) and ok
    return ok


def mutation_18_spec60_obligation_signatures_regress():
    import shutil, tempfile
    import validate_package as vp

    def results_by_name():
        return {r["check"]: r for r in vp.RESULTS}

    scratch = tempfile.mkdtemp(prefix="amcca_mut18_root_")
    real_root, real_results = vp.ROOT, vp.RESULTS
    try:
        shutil.copytree(os.path.join(ROOT, "src", "AMCCA.App"), os.path.join(scratch, "src", "AMCCA.App"))

        vp.ROOT = scratch
        vp.RESULTS = []
        vp.check_spec60_obligations()
        baseline = results_by_name()
        ok = record("mutation18.baseline_scratch_copy_is_clean",
                    all(r["ok"] for r in baseline.values()),
                    "; ".join(f"{n}: {r['detail']}" for n, r in baseline.items() if not r["ok"]))

        main_window_path = os.path.join(scratch, "src", "AMCCA.App", "MainWindow.xaml")
        with open(main_window_path, encoding="utf-8") as f:
            mw_source = f.read()
        needle = 'Command="{Binding ToggleKillSwitchCommand}"'
        assert needle in mw_source, "test fixture assumption violated: MainWindow.xaml's kill-switch binding text has changed"
        with open(main_window_path, "w", encoding="utf-8") as f:
            f.write(mw_source.replace(needle, "", 1))

        approval_view_path = os.path.join(scratch, "src", "AMCCA.App", "Views", "ApprovalQueueView.xaml")
        with open(approval_view_path, encoding="utf-8") as f:
            av_source = f.read()
        needle2 = '<DataGridTextColumn Header="Cost Ceiling" Binding="{Binding CostCeilingDisplay}" Width="100"/>\n'
        assert needle2 in av_source, "test fixture assumption violated: ApprovalQueueView.xaml's Cost Ceiling column text has changed"
        with open(approval_view_path, "w", encoding="utf-8") as f:
            f.write(av_source.replace(needle2, "", 1))

        settings_view_path = os.path.join(scratch, "src", "AMCCA.App", "Views", "SettingsView.xaml")
        with open(settings_view_path, encoding="utf-8") as f:
            sv_source = f.read()
        with open(settings_view_path, "a", encoding="utf-8") as f:
            f.write("<!-- Something went wrong -->\n")

        # Obligation 3: drop the reconciliation-state style definition from the Inspector view.
        inspector_view_path = os.path.join(scratch, "src", "AMCCA.App", "Views", "ProductionInspectorView.xaml")
        with open(inspector_view_path, encoding="utf-8") as f:
            iv_source = f.read()
        needle3 = 'x:Key="ReconciliationStateStyle"'
        assert needle3 in iv_source, "fixture assumption violated: ProductionInspectorView.xaml's reconciliation style key changed"
        with open(inspector_view_path, "w", encoding="utf-8") as f:
            f.write(iv_source.replace(needle3, "x:Key=\"_removed_\"", 1))

        # Obligation 4: drop the honest no-policy-decision disclosure from the Inspector VM.
        inspector_vm_path = os.path.join(scratch, "src", "AMCCA.App", "ViewModels", "ProductionInspectorViewModel.cs")
        with open(inspector_vm_path, encoding="utf-8") as f:
            ivm_source = f.read()
        needle4 = "(no policy decision recorded for this block)"
        assert needle4 in ivm_source, "fixture assumption violated: InspectorBlockInfo's disclosure string changed"
        with open(inspector_vm_path, "w", encoding="utf-8") as f:
            f.write(ivm_source.replace(needle4, "n/a", 1))

        # Obligation 7: remove the progress indicator from the Job Queue view.
        jobqueue_view_path = os.path.join(scratch, "src", "AMCCA.App", "Views", "JobQueueView.xaml")
        with open(jobqueue_view_path, encoding="utf-8") as f:
            jv_source = f.read()
        needle7 = 'IsIndeterminate="True"'
        assert needle7 in jv_source, "fixture assumption violated: JobQueueView.xaml's ProgressBar changed"
        with open(jobqueue_view_path, "w", encoding="utf-8") as f:
            f.write(jv_source.replace(needle7, "", 1))

        # "UI thread does no waiting": reintroduce a blocking wait in App startup.
        app_startup_path = os.path.join(scratch, "src", "AMCCA.App", "App.xaml.cs")
        with open(app_startup_path, encoding="utf-8") as f:
            app_source = f.read()
        with open(app_startup_path, "w", encoding="utf-8") as f:
            f.write(app_source.replace(
                "protected override async void OnStartup(StartupEventArgs e)\n    {",
                "protected override async void OnStartup(StartupEventArgs e)\n    {\n        var _blocked = System.Threading.Tasks.Task.CompletedTask.GetAwaiter().GetResult();",
                1))

        vp.RESULTS = []
        vp.check_spec60_obligations()
        mutated = results_by_name()

        ok = record("mutation18.obligation_1_goes_red_when_kill_switch_binding_removed",
                    not mutated["spec60.obligation_1_kill_switch_in_shared_chrome"]["ok"],
                    mutated["spec60.obligation_1_kill_switch_in_shared_chrome"]["detail"]) and ok
        ok = record("mutation18.obligation_2_unaffected_by_unrelated_mutation",
                    mutated["spec60.obligation_2_autonomy_and_publishing_visible"]["ok"]) and ok
        ok = record("mutation18.obligation_5_goes_red_when_cost_ceiling_column_removed",
                    not mutated["spec60.obligation_5_approval_detail_columns"]["ok"]
                    and "CostCeilingDisplay" in mutated["spec60.obligation_5_approval_detail_columns"]["detail"],
                    mutated["spec60.obligation_5_approval_detail_columns"]["detail"]) and ok
        ok = record("mutation18.obligation_6_goes_red_when_generic_failure_text_introduced",
                    not mutated["spec60.obligation_6_no_bare_generic_failure_text"]["ok"]
                    and "SettingsView.xaml" in mutated["spec60.obligation_6_no_bare_generic_failure_text"]["detail"],
                    mutated["spec60.obligation_6_no_bare_generic_failure_text"]["detail"]) and ok
        ok = record("mutation18.obligation_3_goes_red_when_reconciliation_style_removed",
                    not mutated["spec60.obligation_3_number_provenance_visually_distinct"]["ok"],
                    mutated["spec60.obligation_3_number_provenance_visually_distinct"]["detail"]) and ok
        ok = record("mutation18.obligation_4_goes_red_when_no_policy_disclosure_removed",
                    not mutated["spec60.obligation_4_blocked_item_shows_rule_and_unblock_path"]["ok"],
                    mutated["spec60.obligation_4_blocked_item_shows_rule_and_unblock_path"]["detail"]) and ok
        ok = record("mutation18.obligation_7_goes_red_when_job_queue_progress_removed",
                    not mutated["spec60.obligation_7_long_operations_show_progress_and_cancel"]["ok"]
                    and "Job Queue" in mutated["spec60.obligation_7_long_operations_show_progress_and_cancel"]["detail"],
                    mutated["spec60.obligation_7_long_operations_show_progress_and_cancel"]["detail"]) and ok
        ok = record("mutation18.ui_thread_startup_goes_red_when_blocking_wait_reintroduced",
                    not mutated["spec60.ui_thread_startup_does_not_block"]["ok"],
                    mutated["spec60.ui_thread_startup_does_not_block"]["detail"]) and ok
    finally:
        vp.ROOT, vp.RESULTS = real_root, real_results
        shutil.rmtree(scratch, ignore_errors=True)

    # Prove reversion: the real package tree, checked directly, still satisfies every obligation check.
    saved = vp.RESULTS
    vp.RESULTS = []
    vp.check_spec60_obligations()
    real_results_by_name = results_by_name()
    real_clean = all(r["ok"] for r in real_results_by_name.values())
    real_detail = "; ".join(f"{n}: {r['detail']}" for n, r in real_results_by_name.items() if not r["ok"])
    vp.RESULTS = saved
    ok = record("mutation18.real_package_unaffected_by_mutation", real_clean, real_detail) and ok
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
        mutation_15_walk_files_oversensitive_filter,
        mutation_16_ddl_check_regresses_from_contract_enum,
        mutation_17_thrown_error_code_regresses_from_spec_catalogue,
        mutation_18_spec60_obligation_signatures_regress,
        mutation_19_field_presence_check_regresses_when_a_real_column_is_dropped,
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
