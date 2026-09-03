#!/usr/bin/env python3
"""
Semantic validation of the in-memory output of generate_artifacts.py, independent of
TOOLS/generate_artifacts.py --check's byte-level disk diff.

--check proves the generator is DETERMINISTIC (same input -> same bytes) and that
nothing on disk has drifted from it. It does NOT prove the generator is CORRECT: a
generator that is deterministic but wrong would produce byte-identical wrong output
forever and --check would happily pass it. This module calls the generation
functions directly and asserts properties that must hold of the *model*, not just of
the *bytes*, so a semantically broken generator fails here even when its output is
internally self-consistent.
"""
import json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "TOOLS"))
import generate_artifacts as ga

RESULTS = []


def check(name, ok, detail=""):
    RESULTS.append((name, bool(ok), detail))
    return ok


def run():
    T = ga.build_transitions()
    errs = ga.validate_state_machine(T)
    check("stm.structurally_valid", not errs, "; ".join(errs[:5]))

    sm_json = ga.build_state_machine_json(T)
    prod_states = [s["name"] for s in sm_json["states"]]

    # -------------------------------------------------------- version semantics
    check("sm.schema_version_matches_SV", sm_json["schema_version"] == ga.SV,
          f"{sm_json['schema_version']} != {ga.SV}")
    m = re.search(r"amcca://data/state-machine/([0-9.]+)$", sm_json["$id"])
    check("sm.id_is_well_formed", bool(m), sm_json["$id"])
    if m:
        check("sm.id_contains_current_version", m.group(1) == ga.SV,
              f"$id version {m.group(1)!r} != SV {ga.SV!r}")

    schemas = ga.build_schemas(prod_states)

    for name, doc in schemas.items():
        idm = re.match(r"^amcca://schema/([a-z-]+)/([0-9.]+)$", doc.get("$id", ""))
        check(f"schema[{name}].id_well_formed", bool(idm), doc.get("$id"))
        if idm:
            check(f"schema[{name}].id_contains_current_version", idm.group(2) == ga.SV,
                  f"{doc['$id']} does not carry {ga.SV}")
        sv_const = doc.get("properties", {}).get("schema_version", {}).get("const")
        check(f"schema[{name}].schema_version_const_matches_SV", sv_const == ga.SV,
              f"{sv_const!r} != {ga.SV!r}")
        # no stale/old version literal anywhere in the serialised document
        blob = json.dumps(doc)
        for old in re.findall(r"\b\d+\.\d+\.\d+\b", blob):
            if old != ga.SV:
                check(f"schema[{name}].no_stale_version_literal[{old}]", False,
                      f"found stale version string {old!r} embedded in {name}.schema.json")

    # ---------------------------------------- cross-reference: production.state enum
    prod_enum = schemas["production"]["properties"]["state"]["enum"]
    check("crossref.production_enum_matches_state_machine", prod_enum == prod_states,
          f"production.schema.json enum ({len(prod_enum)}) != state-machine.json states "
          f"({len(prod_states)})")

    # ---------------------------------------- cross-reference: evidence enums
    # V31.1.2 (item 2): consume ga.AUTHORITATIVE_EVIDENCE / ga.NON_AUTHORITATIVE_EVIDENCE /
    # ga.ALL_EVIDENCE directly -- the generator's actual module-level constants (SV lines
    # 131-133) -- rather than re-deriving or re-declaring the vocabulary as a second
    # literal here. A generator bug that changes what's authoritative must be caught by
    # this cross-reference, not silently agreed with by a parallel reconstruction of it.
    pub_conditional = schemas["publication"]["allOf"][0]
    verified_evidence_enum = pub_conditional["then"]["properties"]["evidence_source"]["enum"]
    all_evidence_enum = schemas["publication"]["properties"]["evidence_source"]["oneOf"][0]["enum"]
    check("crossref.publication_verified_evidence_is_authoritative_only",
          set(verified_evidence_enum) == set(ga.AUTHORITATIVE_EVIDENCE),
          f"{verified_evidence_enum} != {ga.AUTHORITATIVE_EVIDENCE}")
    check("crossref.publication_all_evidence_enum_matches_generator_constant",
          set(all_evidence_enum) == set(ga.ALL_EVIDENCE),
          f"{all_evidence_enum} != {ga.ALL_EVIDENCE}")
    check("crossref.non_authoritative_evidence_excluded_from_verified_enum",
          set(ga.NON_AUTHORITATIVE_EVIDENCE) <= set(all_evidence_enum)
          and not (set(ga.NON_AUTHORITATIVE_EVIDENCE) & set(verified_evidence_enum)),
          f"non_authoritative={ga.NON_AUTHORITATIVE_EVIDENCE} all={all_evidence_enum} "
          f"verified={verified_evidence_enum}")

    # ---------------------------------------- invariant: money is never a float
    money_fields = []
    for name, doc in schemas.items():
        for pname, pdef in doc.get("properties", {}).items():
            defs = pdef.get("oneOf", [pdef])
            for d in defs:
                if isinstance(d, dict) and d.get("pattern", "").startswith("^-?[0-9]") \
                        or (isinstance(d, dict) and d.get("pattern", "").startswith("^[0-9]{1,13}\\.")):
                    if d.get("type") == "string":
                        money_fields.append(f"{name}.{pname}")
    check("invariant.money_fields_are_typed_string_with_decimal_pattern", len(money_fields) > 0,
          "expected at least one money-shaped field (D-023)")
    # None of them may declare type number/float anywhere in the money oneOf branches
    bad_money = []
    for name, doc in schemas.items():
        for pname, pdef in doc.get("properties", {}).items():
            if pname not in ("amount", "estimated_cost", "reserved_cost", "limit_amount"):
                continue
            defs = pdef.get("oneOf", [pdef])
            for d in defs:
                if isinstance(d, dict) and d.get("type") == "number":
                    bad_money.append(f"{name}.{pname}")
    check("invariant.no_money_field_typed_as_number", not bad_money, f"{bad_money}")

    # ---------------------------------------- invariant: synthetic-content gating exists
    pub_props = schemas["publication"]["properties"]
    check("invariant.synthetic_declaration_id_field_exists", "synthetic_declaration_id" in pub_props)
    check("invariant.platform_label_required_field_exists", "platform_label_required" in pub_props)
    label_conditional = schemas["publication"]["allOf"][1]
    check("invariant.label_conditional_requires_declaration_id",
          "synthetic_declaration_id" in label_conditional["then"]["required"])

    # ---------------------------------------- cross-reference: table names vs schemas
    table_names, _doc = ga.build_tables_and_doc()
    check("crossref.tables_nonempty", len(table_names) > 0)
    check("crossref.publications_table_present", "publications" in table_names)
    check("crossref.synthetic_declarations_table_present", "synthetic_declarations" in table_names)
    check("crossref.no_duplicate_table_names", len(table_names) == len(set(table_names)))

    # ---------------------------------------- traceability map covers every SPEC file
    trace = ga.build_traceability(ROOT)
    spec_files = sorted(f for f in os.listdir(os.path.join(ROOT, "SPEC")) if f.endswith(".md"))
    missing = [f for f in spec_files if f not in trace]
    check("crossref.traceability_covers_every_spec_file", not missing, f"{missing[:5]}")

    ok = all(r[1] for r in RESULTS)
    for name, r_ok, detail in RESULTS:
        line = ("PASS" if r_ok else "FAIL") + f"  {name}"
        if detail and not r_ok:
            line += f"  -- {detail}"
        print(line)
    print("-" * 72)
    passed = sum(1 for r in RESULTS if r[1])
    print(f"{passed}/{len(RESULTS)} semantic checks passed")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(run())
