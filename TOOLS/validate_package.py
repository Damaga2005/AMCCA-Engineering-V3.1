#!/usr/bin/env python3
"""
AMCCA package validator.

This is the executable release gate required by D-025 and D-029. A prose document
cannot certify that this package is consistent; this script can, and it is the only
thing permitted to.

Usage:
    python TOOLS/validate_package.py            verify (uses --check semantics for drift)
    python TOOLS/validate_package.py --regen    regenerate derived artifacts first, then verify
    python TOOLS/validate_package.py --json     machine-readable result

Exit code 0 = all checks passed. Non-zero = failing build. There is no prose override.

V3.1 changes (audit applied in full):
  V31-01: drift detection now calls generate_artifacts.check_all(), which regenerates
          every canonical artifact and diffs it byte-for-byte against disk.
  V31-02: every jsonschema validator constructed here uses FormatChecker(), so
          `format: date-time` is actually enforced, not merely declared.
  V31-05: money comparisons use Decimal, never float. A static AST check asserts that
          no call to the builtin `float` appears anywhere in TOOLS/*.py.
"""
import ast, json, os, re, sys, hashlib, argparse, collections
from decimal import Decimal, InvalidOperation

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "TOOLS"))
import generate_artifacts as _ga  # V31.1.2 (item 1): SV comes from the canonical
                                   # generator, never a second hardcoded literal here.
SV = _ga.SV

RESULTS = []

# Status vocabulary (V31.1.1): PASS / FAIL / N/A / UNKNOWN.
#   PASS, FAIL  -- ordinary check(), as before.
#   N/A         -- the check does not apply in this environment/stage (e.g. an
#                   optional validator library is not installed and the thing it
#                   would validate has no other way to be checked). ALWAYS requires
#                   a non-empty justification string -- check_na() enforces this.
#   UNKNOWN     -- reserved for a check that was supposed to run but genuinely could
#                   not be determined either way. There is intentionally no
#                   "default to UNKNOWN on any exception" convenience helper: a
#                   check that silently swallows an exception and reports nothing
#                   (the V31-defect this vocabulary exists to close -- see
#                   check_config()'s config.example_validates_against_schema) is
#                   exactly the "not tested" == "PASS" conflation this fixes. Any
#                   code path that used to `except ...: pass` past a check must
#                   call check_na() (if it is legitimately inapplicable) or
#                   check(name, False, ...) (if it is a real failure) instead of
#                   silently omitting the check from RESULTS.
# release_gate.py enforces: any UNKNOWN status blocks release; N/A is allowed only
# because check_na() cannot be called without a justification.


def check(name, ok, detail=""):
    RESULTS.append({"check": name, "ok": bool(ok), "status": "PASS" if ok else "FAIL", "detail": detail})
    return ok


def check_na(name, justification):
    assert justification, f"N/A check {name!r} requires a non-empty justification"
    RESULTS.append({"check": name, "ok": None, "status": "N/A", "detail": justification})
    return None


def check_unknown(name, detail):
    RESULTS.append({"check": name, "ok": None, "status": "UNKNOWN", "detail": detail})
    return None


def read(*parts):
    with open(os.path.join(ROOT, *parts), encoding="utf-8") as f:
        return f.read()


def get_certified_repository_files(root=None):
    """Canonical single-source definition of the certified repository universe (DEF-CERT-008).

    Deterministically discovers all legitimate repository content files under `root`
    (or ROOT by default). Excludes SCM metadata (.git) and ephemeral build/environment
    directories (.git, __pycache__, .venv, venv, .pytest_cache, bin, obj, artifacts, dist),
    while strictly preserving legitimate root configuration dotfiles like .gitignore,
    and workflow files under .github/.
    """
    base_root = root or ROOT
    skip_dirs = {".git", "__pycache__", ".venv", "venv", ".pytest_cache", "bin", "obj", "artifacts", "dist"}
    skip_files = {".git"}
    certified = []
    for base, dirs, files in os.walk(base_root):
        dirs[:] = [d for d in dirs if d not in skip_dirs]
        for fn in files:
            if fn in skip_files or fn.endswith(".pyc") or fn.endswith(".pyo") or fn.endswith(".swp"):
                continue
            p = os.path.join(base, fn)
            rel = os.path.relpath(p, base_root).replace(os.sep, "/")
            certified.append(rel)
    return sorted(certified)


CERTIFIED_REPOSITORY_FILES = get_certified_repository_files


def walk_files():
    """Generator yielding certified repository files relative to ROOT."""
    for rel in get_certified_repository_files(ROOT):
        yield rel


def _validator_cls():
    from jsonschema import Draft202012Validator
    return Draft202012Validator


def _format_checker():
    from jsonschema import FormatChecker
    return FormatChecker()


def check_schemas():
    sch_dir = os.path.join(ROOT, "SCHEMAS")
    files = sorted(f for f in os.listdir(sch_dir) if f.endswith(".schema.json"))
    check("schemas.present", len(files) >= 15, f"{len(files)} schema files")

    try:
        Validator = _validator_cls()
        have_lib = True
    except ImportError:
        have_lib = False
    check("schemas.validator_available", have_lib,
          "jsonschema not installed; structural validation skipped (pip install jsonschema)")

    all_valid, all_versioned, bad = True, True, []
    for fn in files:
        doc = json.load(open(os.path.join(sch_dir, fn), encoding="utf-8"))
        if have_lib:
            try:
                Validator.check_schema(doc)
            except Exception as e:
                all_valid = False
                bad.append(f"{fn}: {e}")
        props = doc.get("properties", {})
        if "schema_version" not in props:
            all_versioned = False
            bad.append(f"{fn}: missing schema_version property (D-004)")
        elif props["schema_version"].get("const") != SV:
            all_versioned = False
            bad.append(f"{fn}: schema_version const != {SV}")
        if doc.get("additionalProperties") is not False:
            all_valid = False
            bad.append(f"{fn}: additionalProperties must be false (D-004)")

    check("schemas.valid_draft_2020_12", all_valid, "; ".join(bad[:5]))
    check("schemas.every_schema_versioned", all_versioned,
          "D-004: six of nine V2 schemas failed this")
    return files


def check_date_time_enforcement():
    try:
        Validator = _validator_cls()
        fc = _format_checker()
    except ImportError:
        return check("formats.date_time_enforced", False, "jsonschema not installed")

    # This registration check is the reason V31-02 exists: FormatChecker() silently
    # accepts every string for format:"date-time" unless rfc3339-validator is
    # installed, with NO error and NO warning. See TOOLS/requirements.txt.
    check("formats.rfc3339_validator_registered", "date-time" in fc.checkers,
          "rfc3339-validator is not installed; FormatChecker will silently accept "
          "every date-time value. pip install rfc3339-validator (see TOOLS/requirements.txt)")

    probe = {"$schema": "https://json-schema.org/draft/2020-12/schema",
             "type": "object", "properties": {"t": {"type": "string", "format": "date-time"}}}
    v = Validator(probe, format_checker=fc)

    positives = ["2026-09-03T08:41:00Z", "2026-09-03T08:41:00+00:00", "2026-09-03T10:41:00+02:00"]
    negatives = ["NOT-A-DATE", "2026-99-99T00:00:00Z", "2026-09-03",
                 "2026-09-03T99:99:99Z", "2026-09-03 10:00:00"]

    pos_ok = all(not list(v.iter_errors({"t": p})) for p in positives)
    neg_ok = all(list(v.iter_errors({"t": n})) for n in negatives)

    check("formats.date_time_accepts_valid", pos_ok, f"one or more of {positives} was rejected")
    check("formats.date_time_rejects_invalid", neg_ok,
          f"one or more of {negatives} passed validation without FormatChecker (V31-02)")

    src = read("TOOLS", "validate_package.py")
    check("formats.all_validator_constructions_use_format_checker",
          "format_checker=" in src, "at least one Validator(...) call site must pass format_checker")


def check_state_machine():
    sm = json.load(open(os.path.join(ROOT, "SCHEMAS", "state-machine.json"), encoding="utf-8"))
    states = [s["name"] for s in sm["states"]]
    kinds = {s["name"]: s["kind"] for s in sm["states"]}
    terminal = set(sm["terminal_states"])
    trans = sm["transitions"]

    inc, out = collections.defaultdict(set), collections.defaultdict(set)
    ids = []
    for t in trans:
        ids.append(t["id"])
        inc[t["to"]].add(t["from"])
        out[t["from"]].add(t["to"])

    check("stm.unique_transition_ids", len(set(ids)) == len(ids), f"{len(ids)} transitions")
    check("stm.no_self_loops", all(t["from"] != t["to"] for t in trans))
    check("stm.known_endpoints", all(t["from"] in states and t["to"] in states for t in trans))

    no_in = [s for s in states if s != sm["initial_state"] and not inc[s]]
    check("stm.every_state_has_inbound", not no_in, f"orphans: {no_in}")

    dead = [s for s in states if kinds[s] != "terminal" and not out[s]]
    check("stm.every_non_terminal_has_outbound", not dead, f"dead ends: {dead}")

    leaky = [s for s in terminal if out[s]]
    check("stm.terminals_have_no_outbound", not leaky, f"leaky: {leaky}")

    seen, stack = {sm["initial_state"]}, [sm["initial_state"]]
    while stack:
        for n in out[stack.pop()]:
            if n not in seen:
                seen.add(n); stack.append(n)
    unreachable = [s for s in states if s not in seen]
    check("stm.all_reachable_from_init", not unreachable, f"unreachable: {unreachable}")

    rev = collections.defaultdict(set)
    for t in trans:
        rev[t["to"]].add(t["from"])
    seenb, stackb = set(terminal), list(terminal)
    while stackb:
        for p in rev[stackb.pop()]:
            if p not in seenb:
                seenb.add(p); stackb.append(p)
    stuck = [s for s in states if s not in seenb]
    check("stm.all_can_reach_terminal", not stuck, f"stuck: {stuck}")

    prod = json.load(open(os.path.join(ROOT, "SCHEMAS", "production.schema.json"), encoding="utf-8"))
    enum = prod["properties"]["state"].get("enum", [])
    check("stm.production_enum_matches", enum == states, f"enum {len(enum)} vs states {len(states)}")

    matrix = read("SPEC", "13_STATE_TRANSITION_MATRIX.md")
    missing = [t["id"] for t in trans if f"`{t['id']}`" not in matrix]
    check("stm.matrix_lists_all_transitions", not missing, f"missing from SPEC/13: {missing[:5]}")
    return sm


def check_database():
    tables = json.load(open(os.path.join(ROOT, "SCHEMAS", "tables.json"), encoding="utf-8"))["tables"]
    doc = read("SPEC", "11_DATABASE_SCHEMA.md")

    contracted = set(re.findall(r"^### `([a-z_]+)`$", doc, re.M))
    check("db.every_table_has_contract", set(tables) == contracted,
          f"declared {len(tables)}, contracted {len(contracted)}, "
          f"missing {sorted(set(tables) - contracted)[:5]}")

    named = set()
    table_re = re.compile(r"`([a-z][a-z0-9_]{3,})`")
    known = set(tables)
    for rel in walk_files():
        if not rel.endswith(".md"):
            continue
        for m in table_re.findall(read(*rel.split("/"))):
            if m in known:
                named.add(m)
    orphan_refs = named - contracted
    check("db.no_table_referenced_without_contract", not orphan_refs, f"{sorted(orphan_refs)[:5]}")

    for kw in ["TX-1", "TX-8", "Concurrency rules"]:
        check(f"db.contains[{kw}]", kw in doc)

    signed_ok = ("kind = 'ADJUSTMENT' OR amount NOT LIKE '-%'" in doc
                 and "state='REVERSED' OR amount NOT LIKE '-%'" in doc)
    check("db.money_signed_exceptions_explicit", signed_ok,
          "V31-04: only cost_events(ADJUSTMENT) and revenue_events(REVERSED) may be signed")
    nonneg_present = doc.count("NOT LIKE '-%'") >= 4
    check("db.nonnegative_money_checks_present", nonneg_present,
          "V31-04: budgets, reservations, opportunities and cost_events need explicit non-negative CHECKs")
    return tables


# Maps a SCHEMAS/*.schema.json file to the table it governs. Only files whose contract
# describes a column backed by a single physical table are listed; join tables and
# pure-computation schemas (e.g. state-machine.json) are not contracts over one table
# and are out of scope for this check.
_ENUM_CHECK_TABLE_MAP = {
    "agent-run.schema.json": "agent_runs",
    "analytics.schema.json": "analytics_snapshots",
    "audit.schema.json": "audit_log",
    "claim.schema.json": "claims",
    "cost-event.schema.json": "cost_events",
    "event.schema.json": "events",
    "job.schema.json": "jobs",
    "production.schema.json": "productions",
    "publication.schema.json": "publications",
    "qa.schema.json": "qa_reports",
    "referral.schema.json": "referral_links",
    "rights.schema.json": "rights_records",
    "tool-run.schema.json": "tool_runs",
}


def _extract_migrations_from_csharp(cs_source):
    """Pulls (version, up_sql) tuples out of MigrationService.BuiltInMigrations in
    declaration order. The C# literals are verbatim strings (@"...") with no embedded
    double quotes anywhere in this file (SQL uses single-quoted string literals), so a
    non-greedy match on the first following '"' is a safe boundary -- if that ever
    stops being true, this raises rather than silently mis-parsing."""
    pattern = re.compile(
        r'\(\s*(\d+)\s*,\s*"([^"]*)"\s*,\s*@"(.*?)"\s*,\s*@"(.*?)"\s*\)',
        re.S)
    migrations = pattern.findall(cs_source)
    if not migrations:
        raise AssertionError("no migrations parsed out of MigrationService.cs -- parser is broken")
    return [(int(v), up) for v, _name, up, _down in migrations]


def _build_live_schema_via_sqlite(up_sql_statements):
    """Applies every migration's UpSql against a real in-memory SQLite database and
    returns {table_name: create_table_sql}. This is the actual final schema (renames,
    rebuilds and drops all resolved by the engine itself), not a text approximation of
    migration ordering."""
    import sqlite3
    con = sqlite3.connect(":memory:")
    try:
        con.execute("PRAGMA foreign_keys=ON;")
        for up in up_sql_statements:
            con.executescript(up)
        rows = con.execute(
            "SELECT name, sql FROM sqlite_master WHERE type='table' AND sql IS NOT NULL;"
        ).fetchall()
        return {name: sql for name, sql in rows}
    finally:
        con.close()


_CHECK_IN_RE = re.compile(r"CHECK\s*\(\s*(\w+)\s+IN\s*\(([^)]*)\)", re.I)


def _extract_check_domains(table_sql):
    """{column: {allowed values}} for every CHECK(col IN (...)) in one CREATE TABLE
    statement's text. Exposed as its own function (not inlined into the check below)
    so a mutation test can exercise it directly against a scratch-mutated schema
    string without needing to run a whole migration set through SQLite."""
    domains = {}
    for col, vals in _CHECK_IN_RE.findall(table_sql):
        domains.setdefault(col, set()).update(re.findall(r"'([^']*)'", vals))
    return domains


def check_contract_enum_matches_ddl_check():
    """A JSON contract's `enum` on a column is meaningless as a structural guarantee if
    the table's own CHECK constraint does not enforce the same domain (D-026: 'This
    holds even if the preflight code path that is supposed to enforce it has a bug').
    This check builds the real, final schema by actually running every migration
    against SQLite, then compares each mapped contract's enums against that table's
    CHECK(col IN (...)) constraints -- catching both an unconstrained column and a
    contract/DDL contradiction, neither of which any other check here detects."""
    cs_source = read("src", "AMCCA.Core", "Database", "MigrationService.cs")
    try:
        migrations = _extract_migrations_from_csharp(cs_source)
    except AssertionError as e:
        return check("contracts.enum_matches_ddl_check", False, str(e))

    migrations.sort(key=lambda m: m[0])
    live_schema = _build_live_schema_via_sqlite([up for _v, up in migrations])

    mismatches = []

    for schema_file, table in sorted(_ENUM_CHECK_TABLE_MAP.items()):
        schema_path = os.path.join(ROOT, "SCHEMAS", schema_file)
        if not os.path.exists(schema_path):
            continue
        contract = json.loads(read("SCHEMAS", schema_file))
        table_sql = live_schema.get(table)
        if table_sql is None:
            mismatches.append(f"{table}: table not found in the live schema built from MigrationService.cs")
            continue

        ddl_checks = _extract_check_domains(table_sql)

        for prop, spec in (contract.get("properties") or {}).items():
            if not (isinstance(spec, dict) and "enum" in spec):
                continue
            contract_vals = set(spec["enum"])
            ddl_vals = ddl_checks.get(prop)
            if ddl_vals is None:
                if re.search(rf"^\s*{re.escape(prop)}\s+\w", table_sql, re.M):
                    mismatches.append(f"{table}.{prop}: contract has {len(contract_vals)} enum values, DDL column has no CHECK")
                # else: column doesn't exist on this table at all -- a different
                # concern (contract field with no storage), not this check's job.
            elif ddl_vals != contract_vals:
                only_contract = sorted(contract_vals - ddl_vals)
                only_ddl = sorted(ddl_vals - contract_vals)
                detail = f"{table}.{prop}: DIVERGES"
                if only_contract:
                    detail += f"; contract allows and DDL rejects {only_contract}"
                if only_ddl:
                    detail += f"; DDL allows and contract rejects {only_ddl}"
                mismatches.append(detail)

    check("contracts.enum_matches_ddl_check", not mismatches,
          "; ".join(mismatches) if mismatches else "")


def check_thrown_error_codes_catalogued():
    """refs.all_error_codes_catalogued (in check_references) only scans .md prose for
    `AMCCA-XXX-NNN`-shaped citations -- it never looks at the .cs that actually throws a
    code, so a code thrown by real code but never once mentioned in a SPEC file passes it
    silently (fourth audit, section 2.4: this blind spot was found by accident while
    writing the audit itself, when citing the six codes it found put the citation check
    into red). This closes that gap: it resolves every `AmccaErrors.Xxx` a
    `throw new AmccaException(...)` call site actually uses -- including a `= OtherConstant;`
    alias like Cst002 -- back to its literal "AMCCA-..." value, and requires that value be
    catalogued in SPEC/05."""
    errors_cs = read("src", "AMCCA.Core", "Contracts", "AmccaErrors.cs")
    const_map = {}
    aliases = {}
    for name, val in re.findall(r'public const string (\w+)\s*=\s*(".*?"|\w+)\s*;', errors_cs):
        if val.startswith('"'):
            const_map[name] = val.strip('"')
        else:
            aliases[name] = val
    for name, ref in aliases.items():
        const_map[name] = const_map.get(ref)

    src_dir = os.path.join(ROOT, "src")
    thrown_consts = set()
    for dirpath, dirnames, filenames in os.walk(src_dir):
        dirnames[:] = [d for d in dirnames if d not in (".git", "bin", "obj")]
        for fn in filenames:
            if not fn.endswith(".cs"):
                continue
            with open(os.path.join(dirpath, fn), encoding="utf-8") as f:
                text = f.read()
            thrown_consts |= set(re.findall(
                r'throw\s+(?:new\s+)?AmccaException\(\s*AmccaErrors\.(\w+)', text))

    thrown_codes = {const_map[c] for c in thrown_consts if const_map.get(c)}
    catalogued = set(re.findall(r"`(AMCCA-[A-Z]{2,4}-\d{3})`", read("SPEC", "05_ERROR_MODEL.md")))
    check("refs.all_thrown_error_codes_catalogued", thrown_codes <= catalogued,
          f"thrown by code but uncatalogued: {sorted(thrown_codes - catalogued)}")


def check_spec():
    spec_dir = os.path.join(ROOT, "SPEC")
    files = sorted(f for f in os.listdir(spec_dir) if f.endswith(".md"))
    nums = [f[:2] for f in files]
    dups = [n for n, c in collections.Counter(nums).items() if c > 1]
    check("spec.unique_numbering", not dups, f"duplicates: {dups}")

    for word, limit in [("DEFINITION_OF_DONE", 1), ("TESTING_STRATEGY", 1)]:
        n = sum(1 for f in files if word in f)
        check(f"spec.single[{word}]", n == limit, f"found {n} (D-022)")

    trace = read("BLUEPRINT", "11_TRACEABILITY.md")
    unmapped = [f for f in files if f not in trace]
    check("spec.every_file_in_traceability", not unmapped, f"{unmapped[:5]}")

    missing_h = [f for f in files if "Normative language" not in read("SPEC", f)]
    check("spec.normative_header_present", not missing_h, f"{missing_h[:5]}")
    return files


def check_references():
    file_set = set(walk_files())
    spec_nums = {f[:2] for f in os.listdir(os.path.join(ROOT, "SPEC")) if f.endswith(".md")}
    bp_nums = {f[:2] for f in os.listdir(os.path.join(ROOT, "BLUEPRINT")) if f.endswith(".md")}

    bad = []
    for rel in file_set:
        if not rel.endswith(".md"):
            continue
        body = read(*rel.split("/"))
        for ref in re.findall(r"`SPEC/(\d{2})", body):
            if ref not in spec_nums:
                bad.append(f"{rel} -> SPEC/{ref}")
        for ref in re.findall(r"`BLUEPRINT/(\d{2})", body):
            if ref not in bp_nums:
                bad.append(f"{rel} -> BLUEPRINT/{ref}")
        for ref in re.findall(r"`SCHEMAS/([a-z\-]+\.(?:schema\.)?json)`", body):
            if f"SCHEMAS/{ref}" not in file_set:
                bad.append(f"{rel} -> SCHEMAS/{ref}")
        for ref in re.findall(r"`(POLICIES/[A-Z_]+\.md)`", body):
            if ref not in file_set:
                bad.append(f"{rel} -> {ref}")
        for ref in re.findall(r"`(CONFIG/[a-z._]+\.yaml)`", body):
            if ref not in file_set:
                bad.append(f"{rel} -> {ref}")
    check("refs.all_internal_references_resolve", not bad, f"{bad[:8]}")

    dec = read("DECISIONS.md")
    declared = set(re.findall(r"^### (D-\d{3})", dec, re.M))
    used = set()
    for rel in file_set:
        if rel.endswith(".md"):
            used |= set(re.findall(r"\b(D-\d{3})\b", read(*rel.split("/"))))
    check("refs.all_decision_ids_exist", used <= declared, f"undefined: {sorted(used - declared)}")

    inv = read("BLUEPRINT", "10_OPERATIONAL_INVARIANTS.md")
    inv_declared = set(re.findall(r"\| (I-\d{2}) \|", inv))
    inv_used = set()
    for rel in file_set:
        if rel.endswith(".md"):
            inv_used |= set(re.findall(r"\b(I-\d{2})\b", read(*rel.split("/"))))
    check("refs.all_invariant_ids_exist", inv_used <= inv_declared, f"undefined: {sorted(inv_used - inv_declared)}")

    err = read("SPEC", "05_ERROR_MODEL.md")
    catalogued = set(re.findall(r"`(AMCCA-[A-Z]{2,4}-\d{3})`", err))
    used_codes = set()
    for rel in file_set:
        if rel.endswith(".md") and not rel.endswith("05_ERROR_MODEL.md"):
            used_codes |= set(re.findall(r"`(AMCCA-[A-Z]{2,4}-\d{3})`", read(*rel.split("/"))))
    check("refs.all_error_codes_catalogued", used_codes <= catalogued, f"uncatalogued: {sorted(used_codes - catalogued)}")

    # AUDIT/ and IMPLEMENTATION_SUMMARY.md are historical change-log prose: they
    # legitimately name the old PUBLIC_URL_CHECK value when describing the V31-06
    # rename itself, which is not the same defect as a live spec/schema/config file
    # still using the retired name.
    leftover = []
    for rel in file_set:
        if rel.endswith((".md", ".json", ".yaml")) and "AUDIT/" not in rel \
                and rel != "IMPLEMENTATION_SUMMARY.md":
            if "PUBLIC_URL_CHECK" in read(*rel.split("/")):
                leftover.append(rel)
    check("refs.no_leftover_public_url_check", not leftover,
          f"V31-06 renamed PUBLIC_URL_CHECK to POST_PUBLISH_CHECK; still present in {leftover[:5]}")


def check_config():
    try:
        import yaml
    except ImportError:
        return check("config.yaml_lib_available", False, "pyyaml not installed")
    cfg_dir = os.path.join(ROOT, "CONFIG")
    ok = True
    for fn in sorted(os.listdir(cfg_dir)):
        if not fn.endswith(".yaml"):
            continue
        try:
            yaml.safe_load(open(os.path.join(cfg_dir, fn), encoding="utf-8"))
        except Exception as e:
            ok = False
            check(f"config.parse[{fn}]", False, str(e))
    check("config.all_yaml_parses", ok)

    example = yaml.safe_load(read("CONFIG", "config.example.yaml"))
    budgets_file = yaml.safe_load(read("CONFIG", "budgets.yaml"))
    check("config.single_budget_vocabulary", set(example["budgets"]) == set(budgets_file["budgets"]))

    def dec(x):
        try:
            return Decimal(str(x))
        except InvalidOperation:
            raise ValueError(f"not a valid decimal: {x!r}")

    b = example["budgets"]
    check("config.budget_consistency_rule",
          dec(b["per_production"]) <= dec(b["daily"]) <= dec(b["monthly"]),
          f"per_production={b['per_production']} daily={b['daily']} monthly={b['monthly']}")
    check("config.budgets_are_non_negative",
          all(dec(b[k]) >= 0 for k in ("per_production", "per_rework", "per_recovery", "daily", "monthly")),
          "V31-04")
    check("config.threshold_ordering", b["warn_percent"] < b["pause_percent"] < b["block_percent"] <= 100)
    check("config.safe_defaults",
          example["publishing_enabled"] is False
          and example["autonomy_mode"] == "MANUAL"
          and example["dry_run"] is True, "D-020")
    check("config.no_literal_secret",
          str(example["providers"]["gateway"]["api_key_secret_ref"]).startswith("secret://"), "D-009")
    check("config.gateway_unverified_by_default",
          example["providers"]["gateway"]["capabilities_verified"] is False, "D-028")

    if os.path.exists(os.path.join(ROOT, "SCHEMAS", "config.schema.json")):
        try:
            Validator = _validator_cls()
            fc = _format_checker()
            schema = json.load(open(os.path.join(ROOT, "SCHEMAS", "config.schema.json"), encoding="utf-8"))
            errs = sorted(Validator(schema, format_checker=fc).iter_errors(example), key=lambda e: e.path)
            check("config.example_validates_against_schema", not errs,
                  "; ".join(f"{list(e.path)}: {e.message}" for e in errs[:3]))
        except ImportError:
            # V31.1.1: this used to `pass` here, which silently dropped the check
            # from RESULTS entirely -- neither PASS nor FAIL, just invisible, which
            # is the "not tested" == "PASS" conflation this vocabulary exists to
            # close. jsonschema's own absence is already caught elsewhere
            # (schemas.validator_available); this is legitimately inapplicable in
            # that specific environment, so it is N/A with a justification, not
            # silently omitted.
            check_na("config.example_validates_against_schema",
                     "jsonschema not installed; already reported by schemas.validator_available")
    else:
        check_na("config.example_validates_against_schema",
                 "SCHEMAS/config.schema.json does not exist in this package layout")

    plats = yaml.safe_load(read("CONFIG", "platforms.yaml"))
    missing_ev = [p for p, v in plats["platforms"].items()
                  if not v.get("synthetic_label", {}).get("source_ref")
                  or not v.get("synthetic_label", {}).get("retrieved_at")]
    check("config.platform_rules_carry_evidence", not missing_ev, f"{missing_ev}")


def check_v2_gap_closure():
    body = "\n".join(read(*r.split("/")) for r in walk_files() if r.endswith((".md", ".yaml")))
    for term in ["synthetic", "AI-generated", "C2PA", "AI Act", "personal data",
                 "watermark", "GDPR", "disclosure"]:
        check(f"gaps.covers[{term}]", term.lower() in body.lower())


def compute_manifest():
    entries = []
    for rel in sorted(walk_files()):
        if rel in ("MANIFEST.md", "MANIFEST.sha256"):
            continue
        with open(os.path.join(ROOT, *rel.split("/")), "rb") as f:
            data = f.read()
        entries.append((rel, hashlib.sha256(data).hexdigest(), len(data)))
    return entries


def write_manifest():
    entries = compute_manifest()
    total = sum(e[2] for e in entries)
    lines = [
        "# Package Manifest", "",
        "> **Generated artifact.** Emitted by `TOOLS/validate_package.py --regen`.", "",
        "`MANIFEST.md` and `MANIFEST.sha256` are **excluded from this manifest**. A file cannot contain",
        "its own hash; V2 shipped a manifest that listed itself, so that entry could never verify.", "",
        f"**Package version:** {SV}", f"**Files:** {len(entries)}", f"**Total bytes:** {total}", "",
        "| File | SHA-256 | Bytes |", "|---|---|--:|",
    ]
    lines += [f"| `{r}` | `{h}` | {b} |" for r, h, b in entries]
    lines.append("")
    open(os.path.join(ROOT, "MANIFEST.md"), "w", encoding="utf-8").write("\n".join(lines))
    open(os.path.join(ROOT, "MANIFEST.sha256"), "w", encoding="utf-8").write(
        "".join(f"{h}  {r}\n" for r, h, _ in entries))
    return entries


def check_manifest():
    path = os.path.join(ROOT, "MANIFEST.md")
    if not os.path.exists(path):
        return check("manifest.present", False, "run --regen")
    body = read("MANIFEST.md")
    sha_body = read("MANIFEST.sha256")
    # P2 hardening: check both generated files, not just MANIFEST.md's prose table --
    # a self-reference could in principle land in MANIFEST.sha256's own line format
    # even if MANIFEST.md's markdown table were clean.
    check("manifest.excludes_itself",
          "| `MANIFEST.md` |" not in body and "| `MANIFEST.sha256` |" not in body
          and "  MANIFEST.md\n" not in sha_body and "  MANIFEST.sha256\n" not in sha_body)
    listed = dict((m[0], m[1]) for m in re.findall(r"\| `([^`]+)` \| `([a-f0-9]{64})` \|", body))
    actual = {r: h for r, h, _ in compute_manifest()}
    mismatched = [r for r in set(listed) & set(actual) if listed[r] != actual[r]]
    check("manifest.matches_tree", listed == actual,
          f"listed {len(listed)} actual {len(actual)}; "
          f"missing {sorted(set(actual) - set(listed))[:3]}; "
          f"stale {sorted(set(listed) - set(actual))[:3]}; "
          f"mismatched {sorted(mismatched)[:3]}")


def check_drift():
    try:
        import generate_artifacts
    except ImportError as e:
        return check("drift.generator_importable", False, str(e))
    ok, diffs = generate_artifacts.check_all(ROOT)
    check("drift.no_generated_artifact_drift", ok, "; ".join(diffs[:5]) if diffs else "")


def check_no_float_in_tooling():
    tools_dir = os.path.join(ROOT, "TOOLS")
    offenders = []
    for fn in sorted(os.listdir(tools_dir)):
        if not fn.endswith(".py"):
            continue
        path = os.path.join(tools_dir, fn)
        src = open(path, encoding="utf-8").read()
        tree = ast.parse(src, filename=fn)
        for node in ast.walk(tree):
            if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == "float":
                offenders.append(f"{fn}:{node.lineno}")
    check("money.no_float_calls_in_tooling", not offenders,
          f"V31-05: float() must never be used for money; found {offenders}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--regen", action="store_true")
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    if args.regen:
        try:
            import generate_artifacts
            artifacts = generate_artifacts.generate_all(ROOT)
            generate_artifacts.write_all(ROOT, artifacts)
        except ImportError:
            pass

    check_schemas()
    check_date_time_enforcement()
    check_state_machine()
    check_database()
    check_contract_enum_matches_ddl_check()
    check_thrown_error_codes_catalogued()
    check_spec()
    check_references()
    check_config()
    check_v2_gap_closure()
    check_drift()
    check_no_float_in_tooling()

    if args.regen:
        write_manifest()
    check_manifest()

    # V31.1.1: FAIL and UNKNOWN both block; N/A does not (it always carries a
    # justification -- check_na() enforces that at the call site).
    failed = [r for r in RESULTS if r["status"] in ("FAIL", "UNKNOWN")]
    if args.json:
        print(json.dumps({"package_version": SV, "checks": len(RESULTS),
                          "failed": len(failed), "results": RESULTS}, indent=2))
    else:
        for r in RESULTS:
            tag = {"PASS": "PASS  ", "FAIL": "FAIL  ", "N/A": "N/A   ", "UNKNOWN": "UNKNOWN "}[r["status"]]
            print(tag + r["check"] + (f"   -- {r['detail']}" if r["detail"] and r["status"] != "PASS" else ""))
        print("-" * 72)
        passed = sum(1 for r in RESULTS if r["status"] == "PASS")
        print(f"{passed}/{len(RESULTS)} checks passed "
              f"({len(failed)} failed, "
              f"{sum(1 for r in RESULTS if r['status'] == 'N/A')} N/A)")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
