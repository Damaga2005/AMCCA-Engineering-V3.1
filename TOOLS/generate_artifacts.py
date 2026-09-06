#!/usr/bin/env python3
"""
AMCCA canonical artifact generator.

Implements D-025 for real: this is the ONLY place the canonical models for the
state machine, the JSON schemas, the database contract and the traceability map
are defined. Every one of those artifacts is *derived* from the functions below.

    python TOOLS/generate_artifacts.py --regen   write the derived artifacts to disk
    python TOOLS/generate_artifacts.py --check   generate in memory, diff against
                                                  what is on disk, fail on any byte
                                                  difference (this is the release gate)

Before V3.1 (defect V31-01): `--regen` existed but nothing compared its output to
the checked-in files, so a hand-edit of a generated artifact could pass validation.
`--check` closes that: it trusts nothing that is merely "marked generated" in a
comment, and diffs real bytes.
"""
import json, os, sys, hashlib, argparse, collections

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SV = "3.1.0"

# ===================================================================
# Shared primitives (V31-04: NonNegativeMoney vs SignedMoney)
# ===================================================================

ULID = {"type": "string", "pattern": "^[0-9A-HJKMNP-TV-Z]{26}$",
        "description": "ULID, generated locally. External identifiers are never primary keys (D-003)."}
ULID_N = {"oneOf": [ULID, {"type": "null"}]}
TS = {"type": "string", "format": "date-time",
      "description": "RFC 3339 timestamp in UTC with explicit offset. format:date-time is enforced by "
                      "FormatChecker at every validation call site (V31-02); it is not merely declared."}
TS_N = {"oneOf": [TS, {"type": "null"}]}
SHA = {"type": "string", "pattern": "^[a-f0-9]{64}$"}
SHA_N = {"oneOf": [SHA, {"type": "null"}]}

NONNEG_MONEY = {
    "type": "string", "pattern": "^[0-9]{1,13}\\.[0-9]{6}$",
    "description": "Decimal string, six fractional digits, NEVER negative. Used for budgets, "
                    "estimates, reservations and settlements, none of which are conceptually "
                    "signed quantities (V31-04). Money is NEVER a float (D-023)."
}
SIGNED_MONEY = {
    "type": "string", "pattern": "^-?[0-9]{1,13}\\.[0-9]{6}$",
    "description": "Decimal string, six fractional digits, MAY be negative. Reserved for cost "
                    "events whose kind can be a signed accounting adjustment (ADJUSTMENT). "
                    "Money is NEVER a float (D-023)."
}
CUR = {"type": "string", "pattern": "^[A-Z]{3}$"}


def base(name, title, req, props, desc):
    p = {"schema_version": {"const": SV,
                            "description": "Contract version. Required on every persisted object (D-004)."}}
    p.update(props)
    return {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$id": f"amcca://schema/{name}/{SV}",
        "title": title,
        "description": desc,
        "type": "object",
        "additionalProperties": False,
        "required": ["schema_version"] + req,
        "properties": p,
    }


# ===================================================================
# 1. State machine (canonical model)
# ===================================================================

STATES = [
 ("INIT",                   "initial",   "Production record created; nothing generated yet."),
 ("RESEARCHING",            "producing", "Research engine gathering timestamped evidence."),
 ("RESEARCH_VERIFIED",      "verified",  "Claims linked to sources; evidence thresholds met."),
 ("CONCEPT_SELECTED",       "gate",      "Strategy decision recorded; concept locked."),
 ("SCRIPTING",              "producing", "Script agent generating script versions."),
 ("SCRIPT_VERIFIED",        "verified",  "Script passes schema, factual and policy validation."),
 ("STORYBOARDING",          "producing", "Storyboard agent generating scene plan."),
 ("STORYBOARD_VERIFIED",    "verified",  "Storyboard structurally valid and script-aligned."),
 ("ASSET_GENERATION",       "producing", "Visual assets generated or sourced."),
 ("ASSETS_READY",           "verified",  "All required assets present with GREEN rights."),
 ("AUDIO_GENERATION",       "producing", "Voice and music tracks generated."),
 ("AUDIO_READY",            "verified",  "Audio passes deterministic technical checks."),
 ("EDITING",                "producing", "MediaWorker composing the candidate render."),
 ("CANDIDATE_RENDERED",     "verified",  "Render artifact exists, hashed and manifest-consistent."),
 ("TECHNICAL_QA",           "qa",        "Container, codec, duration, decode integrity."),
 ("VISUAL_QA",              "qa",        "Black/freeze frames, safe areas, visual coherence."),
 ("AUDIO_QA",               "qa",        "Silence, clipping, loudness, A/V sync, intelligibility."),
 ("CONTENT_QA",             "qa",        "Factual accuracy, tone, policy and claim substantiation."),
 ("RETENTION_QA",           "qa",        "Hook strength and retention heuristics."),
 ("COMPLIANCE_QA",          "qa",        "Rights, disclosure, synthetic labelling, platform policy."),
 ("SCORING",                "gate",      "Aggregate scoring against configured thresholds."),
 ("REWORK",                 "control",   "Targeted regeneration of the earliest invalid DAG node."),
 ("FINAL_VERIFIED",         "verified",  "All gates passed; production manifest sealed."),
 ("READY_TO_PUBLISH",       "gate",      "Publication preflight passed for at least one target."),
 ("PUBLISHING",             "publish",   "Publication intents dispatched to platform adapters."),
 ("PUBLICATION_PROCESSING", "publish",   "At least one target accepted and is processing."),
 ("PUBLICATION_VERIFIED",   "publish",   "All required targets verified by authoritative evidence."),
 ("ARCHIVED",               "terminal",  "Lifecycle closed; artifacts under retention policy."),
 ("BLOCKED",                "control",   "Halted by policy, budget, rights, credential or kill switch."),
 ("FAILED",                 "terminal",  "Permanently failed; not resumable without a new production."),
 ("CANCELLED",              "terminal",  "Cancelled by an authorised operator."),
 ("UNKNOWN_EXTERNAL_STATE", "control",   "An external side effect may or may not have taken place."),
]
KIND = {n: k for n, k, _ in STATES}
DESC_ = {n: d for n, _, d in STATES}
ALL_STATES = [n for n, _, _ in STATES]
TERMINAL = [n for n in ALL_STATES if KIND[n] == "terminal"]
NON_TERMINAL = [n for n in ALL_STATES if KIND[n] != "terminal"]
EXTERNAL = [n for n in ALL_STATES if KIND[n] in ("producing", "qa", "publish")]
REGENERABLE = ["RESEARCHING", "SCRIPTING", "STORYBOARDING",
               "ASSET_GENERATION", "AUDIO_GENERATION", "EDITING"]
REWORKABLE = ["SCRIPTING", "STORYBOARDING", "ASSET_GENERATION", "AUDIO_GENERATION",
              "CANDIDATE_RENDERED", "TECHNICAL_QA", "VISUAL_QA", "AUDIO_QA",
              "CONTENT_QA", "RETENTION_QA", "COMPLIANCE_QA", "SCORING",
              "READY_TO_PUBLISH", "PUBLICATION_PROCESSING"]

# ===================================================================
# Evidence vocabulary (canonical model). Module-level so both the JSON Schema
# builder (build_schemas) and the canonical SQL DDL builder (build_canonical_ddl)
# read the exact same lists instead of each re-declaring their own copy.
# ===================================================================

PUBLICATION_STATES = ["INTENT_CREATED", "UPLOAD_REQUESTED", "UPLOADED", "PROCESSING",
                       "PUBLISHED", "VERIFIED", "REJECTED", "FAILED",
                       "UNKNOWN_EXTERNAL_STATE", "CANCELLED"]
# V31-06: evidence_source is split into an authoritative subset (sufficient for
# VERIFIED) and a broader discovery-only set (never sufficient for VERIFIED).
AUTHORITATIVE_EVIDENCE = ["OFFICIAL_API", "OFFICIAL_DASHBOARD", "OPERATOR_CONFIRMATION"]
NON_AUTHORITATIVE_EVIDENCE = ["POST_PUBLISH_CHECK"]  # renamed from PUBLIC_URL_CHECK
ALL_EVIDENCE = AUTHORITATIVE_EVIDENCE + NON_AUTHORITATIVE_EVIDENCE

# V31-09: platform_capabilities has no dedicated JSON schema (see SPEC/11); its
# evidence vocabulary is wider than publication's because discovery-only statuses
# other than VERIFIED are legitimate resting states for a capability probe.
PLATFORM_CAPABILITY_STATUS = ["DISCOVERED", "VERIFIED", "UNVERIFIED", "DISABLED", "UNSUPPORTED"]
PLATFORM_CAPABILITY_AUTHORITATIVE_EVIDENCE = [
    "OFFICIAL_API", "OFFICIAL_DASHBOARD", "OFFICIAL_DOCUMENTATION",
    "DIRECT_PLATFORM_PROBE", "OPERATOR_CONFIRMATION",
]

O, OP, REC = "Orchestrator", "Operator", "ReconciliationService"


def build_transitions():
    T = []
    def t(tid, f, to, trig, guard, actor=O):
        T.append(dict(id=tid, **{"from": f}, to=to, trigger=trig, guard=guard, actor=actor))

    happy = [
     ("T-001","INIT","RESEARCHING","start_production","Preflight PASS; opportunity_id resolvable; budget reservation for research committed"),
     ("T-002","RESEARCHING","RESEARCH_VERIFIED","research_completed","≥ policy.min_sources independent sources; every material claim has source_id + retrieved_at; no RED source"),
     ("T-003","RESEARCH_VERIFIED","CONCEPT_SELECTED","concept_chosen","Strategy decision persisted with rationale and expected-value snapshot"),
     ("T-004","CONCEPT_SELECTED","SCRIPTING","begin_scripting","Budget reservation for scripting committed; prompt_version pinned"),
     ("T-005","SCRIPTING","SCRIPT_VERIFIED","script_validated","Script matches script schema; every factual line maps to a verified claim; CONTENT_POLICY PASS"),
     ("T-006","SCRIPT_VERIFIED","STORYBOARDING","begin_storyboard","Budget reservation for storyboard committed"),
     ("T-007","STORYBOARDING","STORYBOARD_VERIFIED","storyboard_validated","Scene count > 0; every scene references a script segment; durations sum within tolerance"),
     ("T-008","STORYBOARD_VERIFIED","ASSET_GENERATION","begin_assets","Budget reservation for assets committed; media profile resolved"),
     ("T-009","ASSET_GENERATION","ASSETS_READY","assets_validated","Every storyboard scene has ≥1 asset; every asset has rights_status = GREEN; duplicate check PASS"),
     ("T-010","ASSETS_READY","AUDIO_GENERATION","begin_audio","Budget reservation for audio committed"),
     ("T-011","AUDIO_GENERATION","AUDIO_READY","audio_validated","Voice track decodes; loudness within profile; no clipping; duration aligns to script"),
     ("T-012","AUDIO_READY","EDITING","begin_edit","All upstream artifacts present and hash-verified; disk headroom ≥ config.storage.minimum_free_gb"),
     ("T-013","EDITING","CANDIDATE_RENDERED","render_completed","FFmpeg exit 0; output file hashed; artifact manifest consistent with DAG"),
     ("T-014","CANDIDATE_RENDERED","TECHNICAL_QA","begin_qa","Render artifact readable and probe-able"),
     ("T-015","TECHNICAL_QA","VISUAL_QA","technical_qa_pass","All deterministic technical checks PASS or WARN"),
     ("T-016","VISUAL_QA","AUDIO_QA","visual_qa_pass","Visual checks ≥ threshold; no CRITICAL finding"),
     ("T-017","AUDIO_QA","CONTENT_QA","audio_qa_pass","Audio checks ≥ threshold; no CRITICAL finding"),
     ("T-018","CONTENT_QA","RETENTION_QA","content_qa_pass","Factual accuracy ≥ 8.0; no unsubstantiated material claim"),
     ("T-019","RETENTION_QA","COMPLIANCE_QA","retention_qa_pass","Retention heuristics recorded; no CRITICAL finding"),
     ("T-020","COMPLIANCE_QA","SCORING","compliance_qa_pass","Rights GREEN; required affiliate disclosure present; synthetic-content label present per SPEC/45; platform policy PASS"),
     ("T-021","SCORING","FINAL_VERIFIED","score_accepted","overall_score ≥ policy.qa.overall_min AND every critical dimension ≥ policy.qa.critical_min"),
     ("T-022","FINAL_VERIFIED","READY_TO_PUBLISH","publication_preflight_pass","≥1 target with capability VERIFIED, credential valid, metadata version sealed, referral version valid"),
     ("T-023","READY_TO_PUBLISH","PUBLISHING","dispatch_publication","Publication lock acquired; publication intents persisted; kill switch not engaged; publishing_enabled = true"),
     ("T-024","PUBLISHING","PUBLICATION_PROCESSING","targets_accepted","≥1 publication in {UPLOADED, PROCESSING} and none in UNKNOWN_EXTERNAL_STATE (rollup rule R-2)"),
     ("T-025","PUBLICATION_PROCESSING","PUBLICATION_VERIFIED","all_targets_verified","Every required publication is VERIFIED by authoritative platform evidence (rollup rule R-1); a resolving-URL check alone is never sufficient (V31-06)"),
     ("T-026","PUBLICATION_VERIFIED","ARCHIVED","archive","Analytics baseline captured; retention policy applied"),
    ]
    for row in happy: t(*row)
    t("T-027","FINAL_VERIFIED","ARCHIVED","shelve","Operator archives a verified production without publishing", OP)
    for i, s in enumerate(REWORKABLE, start=1):
        t(f"T-1{i:02d}", s, "REWORK", "defect_detected",
          "≥1 finding with severity ≥ policy.rework.min_severity; responsible artifact resolvable in the DAG; rework attempts < policy.rework.max_attempts")
    for i, s in enumerate(REGENERABLE, start=1):
        t(f"T-2{i:02d}", "REWORK", s, "regenerate_node",
          "Target is the earliest repairable ancestor of the responsible artifact; rework budget reserved; descendants marked SUPERSEDED")
    t("T-2A1","REWORK","FAILED","rework_exhausted","rework attempts = policy.rework.max_attempts OR identical failure signature repeated ≥ 2 times")
    t("T-2A2","REWORK","BLOCKED","rework_budget_exhausted","Rework budget reservation refused; requires authorised budget change")
    t("T-030","RESEARCHING","BLOCKED","insufficient_evidence","Evidence threshold unmet after policy.research.max_attempts; requires operator decision")
    t("T-031","PUBLISHING","FAILED","definitive_rejection","Every target returned a non-retryable rejection with authoritative evidence")
    t("T-032","PUBLICATION_PROCESSING","FAILED","processing_rejected","Every target reported terminal platform-side failure")
    for i, s in enumerate([x for x in NON_TERMINAL if x != "BLOCKED"], start=1):
        t(f"T-3{i:02d}", s, "BLOCKED", "policy_block",
          "Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted")
    for i, s in enumerate([x for x in NON_TERMINAL if x != "BLOCKED"], start=1):
        t(f"T-4{i:02d}", "BLOCKED", s, "resume",
          "target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded", OP)
    t("T-4A1","BLOCKED","FAILED","abandon","Operator abandons a blocked production", OP)
    t("T-4A2","BLOCKED","CANCELLED","cancel","Operator cancels a blocked production", OP)
    for i, s in enumerate(EXTERNAL, start=1):
        t(f"T-5{i:02d}", s, "UNKNOWN_EXTERNAL_STATE", "ambiguous_side_effect",
          "External call was dispatched and no definitive response was obtained; intent row exists; unknown_from is persisted")
    for i, s in enumerate(EXTERNAL, start=1):
        t(f"T-6{i:02d}", "UNKNOWN_EXTERNAL_STATE", s, "reconciled_not_executed",
          "target == productions.unknown_from AND reconciliation proves the side effect did NOT take place", REC)
    t("T-6A1","UNKNOWN_EXTERNAL_STATE","PUBLICATION_PROCESSING","reconciled_accepted","Reconciliation proves the upload was accepted; external_id recovered", REC)
    t("T-6A2","UNKNOWN_EXTERNAL_STATE","PUBLICATION_VERIFIED","reconciled_published","Reconciliation retrieves authoritative published evidence (OFFICIAL_API/OFFICIAL_DASHBOARD/OPERATOR_CONFIRMATION only) for every required target", REC)
    t("T-6A3","UNKNOWN_EXTERNAL_STATE","BLOCKED","unreconcilable","Reconciliation exhausted policy.reconcile.max_attempts without a definitive answer; requires operator")
    t("T-6A4","UNKNOWN_EXTERNAL_STATE","FAILED","reconciled_failed","Reconciliation proves the operation failed definitively and is not repeatable")
    for i, s in enumerate([x for x in NON_TERMINAL if x != "BLOCKED"], start=1):
        t(f"T-7{i:02d}", s, "CANCELLED", "cancel",
          "Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released", OP)
    for i, s in enumerate([x for x in NON_TERMINAL if x not in ("BLOCKED","REWORK","UNKNOWN_EXTERNAL_STATE")], start=1):
        t(f"T-8{i:02d}", s, "FAILED", "permanent_error",
          "Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding")
    return T


def validate_state_machine(T):
    errs = []
    names = set(ALL_STATES)
    ids = [x["id"] for x in T]
    if len(set(ids)) != len(ids):
        dup = [k for k, v in collections.Counter(ids).items() if v > 1]
        errs.append(f"duplicate transition ids: {dup}")
    for x in T:
        if x["from"] not in names: errs.append(f"{x['id']}: unknown from {x['from']}")
        if x["to"] not in names:   errs.append(f"{x['id']}: unknown to {x['to']}")
        if x["from"] == x["to"]:   errs.append(f"{x['id']}: self loop")
    incoming, outgoing = collections.defaultdict(set), collections.defaultdict(set)
    for x in T:
        incoming[x["to"]].add(x["from"]); outgoing[x["from"]].add(x["to"])
    for s in ALL_STATES:
        if s != "INIT" and not incoming[s]: errs.append(f"unreachable (no inbound): {s}")
        if KIND[s] != "terminal" and not outgoing[s]: errs.append(f"dead-end: {s}")
        if KIND[s] == "terminal" and outgoing[s]: errs.append(f"terminal has outbound: {s}")
    seen, stack = {"INIT"}, ["INIT"]
    while stack:
        cur = stack.pop()
        for nxt in outgoing[cur]:
            if nxt not in seen: seen.add(nxt); stack.append(nxt)
    for s in ALL_STATES:
        if s not in seen: errs.append(f"not reachable from INIT: {s}")
    rev = collections.defaultdict(set)
    for x in T: rev[x["to"]].add(x["from"])
    seenb, stackb = set(TERMINAL), list(TERMINAL)
    while stackb:
        cur = stackb.pop()
        for prv in rev[cur]:
            if prv not in seenb: seenb.add(prv); stackb.append(prv)
    for s in ALL_STATES:
        if s not in seenb: errs.append(f"cannot reach terminal: {s}")

    # V3.1.1: a 'verified' state certifies that its upstream verification step
    # actually ran -- so it may only be entered from the specific predecessor(s)
    # that step is allowed to run from, never a shortcut that bypasses it.
    # Two categories of legitimate exception to "only the producing predecessor":
    #   - BLOCKED, via T-4xx (resume): a production blocked while already validly
    #     IN that verified state resumes back into it; this re-enters a state it
    #     legitimately reached before, it does not newly bypass verification.
    #   - PUBLICATION_VERIFIED via UNKNOWN_EXTERNAL_STATE (T-6A2, reconciled_published):
    #     reconciliation IS an authoritative-evidence check, not a bypass of one.
    LEGITIMATE_VERIFIED_PREDECESSORS = {
        "RESEARCH_VERIFIED": {"RESEARCHING", "BLOCKED"},
        "SCRIPT_VERIFIED": {"SCRIPTING", "BLOCKED"},
        "STORYBOARD_VERIFIED": {"STORYBOARDING", "BLOCKED"},
        "ASSETS_READY": {"ASSET_GENERATION", "BLOCKED"},
        "AUDIO_READY": {"AUDIO_GENERATION", "BLOCKED"},
        "CANDIDATE_RENDERED": {"EDITING", "BLOCKED"},
        "FINAL_VERIFIED": {"SCORING", "BLOCKED"},
        "PUBLICATION_VERIFIED": {"PUBLICATION_PROCESSING", "UNKNOWN_EXTERNAL_STATE", "BLOCKED"},
    }
    for s, allowed in LEGITIMATE_VERIFIED_PREDECESSORS.items():
        inbound = {x["from"] for x in T if x["to"] == s}
        illegitimate = inbound - allowed
        if illegitimate:
            errs.append(f"{s}: verification-skipping transition(s) from {sorted(illegitimate)} "
                        f"(only {sorted(allowed)} may enter this verified state)")
    return errs


def build_state_machine_json(T):
    return {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$id": f"amcca://data/state-machine/{SV}",
        "schema_version": SV,
        "aggregate": "production",
        "initial_state": "INIT",
        "terminal_states": TERMINAL,
        "states": [{"name": n, "kind": KIND[n], "description": DESC_[n]} for n in ALL_STATES],
        "transitions": T,
        "rollup_rules": [
            {"id": "R-1", "rule": "production.state = PUBLICATION_VERIFIED iff every publication row whose target is marked required has state = VERIFIED, established only via OFFICIAL_API, OFFICIAL_DASHBOARD or OPERATOR_CONFIRMATION evidence (V31-06)."},
            {"id": "R-2", "rule": "production.state = PUBLICATION_PROCESSING iff at least one required publication is in {UPLOAD_REQUESTED, UPLOADED, PROCESSING, PUBLISHED} and none is in UNKNOWN_EXTERNAL_STATE."},
            {"id": "R-3", "rule": "production.state = UNKNOWN_EXTERNAL_STATE iff at least one required publication is in UNKNOWN_EXTERNAL_STATE."},
            {"id": "R-4", "rule": "production.state = FAILED via T-032 iff every required publication is in {FAILED, REJECTED}."},
            {"id": "R-5", "rule": "A partial outcome (some targets VERIFIED, some FAILED) holds the production in PUBLICATION_PROCESSING and raises an operator notification; it is never silently promoted to PUBLICATION_VERIFIED."}
        ]
    }


def build_state_matrix_md(T):
    inc = collections.Counter(x["to"] for x in T)
    out = collections.Counter(x["from"] for x in T)
    L = []
    L.append("# 13 — Production State Transition Matrix")
    L.append("")
    L.append("> **Generated artifact.** This file is emitted from `TOOLS/generate_artifacts.py`.")
    L.append("> `TOOLS/generate_artifacts.py --check` compares the current file byte-for-byte against a")
    L.append("> fresh generation and fails the release gate on any difference (V31-01). Do not edit it by")
    L.append("> hand; edit the canonical model in `generate_artifacts.py` and run `--regen`.")
    L.append("")
    L.append("> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless documented exception; MAY = optional.")
    L.append("")
    L.append(f"**States:** {len(ALL_STATES)} — **Transitions:** {len(T)} — **Terminal states:** {', '.join(TERMINAL)}")
    L.append("")
    L.append("## Structural guarantees")
    L.append("")
    L.append("The following properties are machine-verified on every build and are release gates (`SPEC/79`, criterion 4):")
    L.append("")
    L.append("1. Every state except `INIT` has at least one inbound transition.")
    L.append("2. Every non-terminal state has at least one outbound transition.")
    L.append("3. No terminal state has an outbound transition.")
    L.append("4. Every state is reachable from `INIT` by forward graph traversal.")
    L.append("5. Every state can reach at least one terminal state.")
    L.append("6. Transition IDs are unique; there are no self-loops.")
    L.append("7. Any transition not listed in this matrix is illegal and MUST fail closed with `AMCCA-STM-001`.")
    L.append("")
    L.append("## State inventory")
    L.append("")
    L.append("| State | Kind | In | Out | Meaning |")
    L.append("|---|---|--:|--:|---|")
    for n in ALL_STATES:
        L.append(f"| `{n}` | {KIND[n]} | {inc[n]} | {out[n]} | {DESC_[n]} |")
    L.append("")
    L.append("## Transition matrix")
    L.append("")
    L.append("`Actor` is the only component permitted to commit the transition. Agents never appear as an actor:")
    L.append("they submit results, the Orchestrator commits state (D-015, invariant I-09).")
    L.append("")
    L.append("| ID | From | To | Trigger | Required evidence / guard | Actor |")
    L.append("|---|---|---|---|---|---|")
    for x in T:
        L.append(f"| `{x['id']}` | `{x['from']}` | `{x['to']}` | `{x['trigger']}` | {x['guard']} | {x['actor']} |")
    L.append("")
    L.append("## Multi-target publication rollup")
    L.append("")
    L.append("A production may target several platforms. The production-level publish states are a **rollup**")
    L.append("of the per-target `publications` rows, never a substitute for them. The rollup is deterministic:")
    L.append("")
    L.append("| Rule | Definition |")
    L.append("|---|---|")
    sm = build_state_machine_json(T)
    for r in sm["rollup_rules"]:
        L.append(f"| `{r['id']}` | {r['rule']} |")
    L.append("")
    L.append("## Persisted transition context")
    L.append("")
    L.append("Three columns on `productions` exist solely to make the control transitions decidable:")
    L.append("")
    L.append("- `blocked_from` — set when entering `BLOCKED`, cleared on resume. `T-4xx` is legal only when the")
    L.append("  target equals this value.")
    L.append("- `unknown_from` — set when entering `UNKNOWN_EXTERNAL_STATE`, cleared on reconciliation. `T-6xx` is")
    L.append("  legal only when the target equals this value.")
    L.append("- `rework_attempts` — bounded by `policy.rework.max_attempts`; `T-2A1` fires on exhaustion.")
    L.append("")
    L.append("Without these columns the resume and reconcile transitions would be ambiguous, which is exactly")
    L.append("the defect this matrix exists to prevent.")
    L.append("")
    return "\n".join(L)


# ===================================================================
# 2. Schemas (canonical model)
# ===================================================================

def build_schemas(prod_states):
    S = {}

    S["production"] = base(
        "production", "Production",
        ["id", "state", "autonomy_mode", "language", "created_at", "updated_at", "aggregate_version"],
        {
            "id": ULID,
            "state": {"enum": prod_states,
                      "description": "Canonical production state. The enum is generated from the same canonical model as SCHEMAS/state-machine.json; the two cannot drift."},
            "blocked_from": {"oneOf": [{"enum": prod_states}, {"type": "null"}]},
            "unknown_from": {"oneOf": [{"enum": prod_states}, {"type": "null"}]},
            "rework_attempts": {"type": "integer", "minimum": 0},
            "aggregate_version": {"type": "integer", "minimum": 0},
            "autonomy_mode": {"enum": ["MANUAL", "ASSISTED", "AUTONOMOUS"]},
            "title": {"type": "string", "maxLength": 500},
            "language": {"type": "string", "pattern": "^[a-z]{2}(-[A-Z]{2})?$"},
            "niche_id": ULID_N, "opportunity_id": ULID_N, "current_manifest_id": ULID_N,
            "created_at": TS, "updated_at": TS,
        },
        "The central aggregate. All other evidence-plane and business-plane records reference it by production_id.")

    # -------------------------------------------------- publication (V31-06, V31-07)
    PUB_STATES = PUBLICATION_STATES

    S["publication"] = base(
        "publication", "Publication (one platform target)",
        ["id", "production_id", "platform", "account_id", "content_version_id",
         "state", "required", "idempotency_key", "platform_label_required",
         "created_at", "updated_at"],
        {
            "id": ULID, "production_id": ULID, "platform": {"type": "string", "minLength": 1, "maxLength": 64},
            "account_id": ULID, "content_version_id": ULID,
            "metadata_version_id": ULID_N, "referral_version_id": ULID_N,
            "state": {"enum": PUB_STATES},
            "required": {"type": "boolean",
                         "description": "Whether this target counts towards the production rollup rules R-1..R-5."},
            "idempotency_key": {"type": "string", "minLength": 16, "maxLength": 200},
            "external_id": {"oneOf": [{"type": "string"}, {"type": "null"}]},
            "external_url": {"oneOf": [{"type": "string", "format": "uri"}, {"type": "null"}]},
            "evidence_source": {
                "oneOf": [{"enum": ALL_EVIDENCE}, {"type": "null"}],
                "description": "How the current state was established. NON_AUTHORITATIVE_EVIDENCE values "
                                "(POST_PUBLISH_CHECK) can support PROCESSING/PUBLISHED but can NEVER, by "
                                "themselves, satisfy the VERIFIED requirement below (V31-06)."
            },
            "evidence_retrieved_at": TS_N,
            # V31-07: the synthetic-declaration link is now structural, not merely procedural.
            "synthetic_declaration_id": {
                **ULID_N,
                "description": "FK to synthetic_declarations. Required whenever platform_label_required "
                                "is true (V31-07)."
            },
            "platform_label_required": {
                "type": "boolean",
                "description": "Denormalised from synthetic_declarations at intent time so this invariant "
                                "is enforceable at the contract level, independent of any join (V31-07)."
            },
            "synthetic_label_applied": {
                "type": "boolean",
                "description": "Whether the platform-native AI-content label was set. See SPEC/45."
            },
            "attempt_count": {"type": "integer", "minimum": 0},
            "last_error_code": {"oneOf": [{"type": "string", "pattern": "^AMCCA-[A-Z]{2,4}-[0-9]{3}$"}, {"type": "null"}]},
            "created_at": TS, "updated_at": TS,
        },
        "One publication target. Production-level publish states are a rollup of these rows, never a replacement.")
    S["publication"]["allOf"] = [
        {
            "if": {"properties": {"state": {"const": "VERIFIED"}}, "required": ["state"]},
            "then": {
                "required": ["evidence_source", "evidence_retrieved_at", "external_id"],
                "properties": {
                    "evidence_source": {"enum": AUTHORITATIVE_EVIDENCE},
                    "evidence_retrieved_at": {"type": "string"},
                    "external_id": {"type": "string"},
                }
            },
            "$comment": "Invariant I-11 (tightened by V31-06): VERIFIED requires evidence_source to be "
                        "one of the authoritative values. POST_PUBLISH_CHECK (a 200 response) is "
                        "syntactically valid for evidence_source in general but is REJECTED here, "
                        "because this conditional's enum excludes it."
        },
        {
            "if": {"properties": {"state": {"const": "VERIFIED"}, "platform_label_required": {"const": True}},
                   "required": ["state", "platform_label_required"]},
            "then": {"required": ["synthetic_declaration_id"],
                     "properties": {"synthetic_declaration_id": {"type": "string"},
                                    "synthetic_label_applied": {"const": True}}},
            "$comment": "Invariant I-18 (made structural by V31-07): VERIFIED is unreachable while a "
                        "required synthetic-content label has not been applied. This holds even if the "
                        "preflight code path that is supposed to prevent it has a bug, because the "
                        "contract itself refuses the object."
        },
    ]

    # -------------------------------------------------------------- job
    S["job"] = base(
        "job", "Durable job",
        ["id", "type", "state", "priority", "idempotency_key", "attempt",
         "max_attempts", "created_at", "updated_at"],
        {
            "id": ULID, "production_id": ULID_N, "type": {"type": "string", "minLength": 1, "maxLength": 64},
            "state": {"enum": ["QUEUED", "LEASED", "RUNNING", "SUCCEEDED", "FAILED",
                               "BLOCKED", "UNKNOWN_EXTERNAL_STATE", "CANCELLED", "DEAD_LETTER"]},
            "priority": {"type": "integer", "minimum": 0, "maximum": 5},
            "idempotency_key": {"type": "string", "minLength": 16, "maxLength": 200},
            "attempt": {"type": "integer", "minimum": 0}, "max_attempts": {"type": "integer", "minimum": 1},
            "scheduled_at": TS_N, "deadline_at": TS_N,
            "lease_owner": {"oneOf": [{"type": "string"}, {"type": "null"}]}, "lease_until": TS_N, "heartbeat_at": TS_N,
            "estimated_cost": {"oneOf": [NONNEG_MONEY, {"type": "null"}],
                               "description": "A cost estimate cannot be negative (V31-04)."},
            "reserved_cost": {"oneOf": [NONNEG_MONEY, {"type": "null"}],
                              "description": "A budget reservation cannot be negative (V31-04)."},
            "currency": CUR, "correlation_id": ULID, "causation_id": ULID_N,
            "last_error_code": {"oneOf": [{"type": "string", "pattern": "^AMCCA-[A-Z]{2,4}-[0-9]{3}$"}, {"type": "null"}]},
            "created_at": TS, "updated_at": TS,
        },
        "Unit of durable execution. A job in LEASED or RUNNING must have lease_owner and lease_until (invariant I-05).")
    S["job"]["allOf"] = [{
        "if": {"properties": {"state": {"enum": ["LEASED", "RUNNING"]}}, "required": ["state"]},
        "then": {"required": ["lease_owner", "lease_until"],
                 "properties": {"lease_owner": {"type": "string"}, "lease_until": {"type": "string"}}},
        "$comment": "Invariant I-05: a job has at most one active lease, and an executing job always has one."
    }]

    # -------------------------------------------------------------- event
    S["event"] = base(
        "event", "Domain event (append-only)",
        ["event_id", "event_type", "aggregate_type", "aggregate_id", "aggregate_version",
         "correlation_id", "occurred_at", "payload"],
        {
            "event_id": ULID,
            "event_type": {"type": "string", "pattern": "^[a-z]+(\\.[a-z_]+)+$"},
            "aggregate_type": {"enum": ["production", "publication", "job", "budget", "policy",
                                        "platform_account", "referral", "experiment", "artifact"]},
            "aggregate_id": ULID, "aggregate_version": {"type": "integer", "minimum": 0},
            "correlation_id": ULID, "causation_id": ULID_N,
            "transition_id": {"oneOf": [{"type": "string", "pattern": "^T-[0-9A-Z]{3}$"}, {"type": "null"}]},
            "occurred_at": TS, "payload": {"type": "object"},
        },
        "Append-only operational history. Separate from the audit log (D-018).")

    # -------------------------------------------------------------- audit
    S["audit"] = base(
        "audit", "Audit record",
        ["audit_id", "action", "actor_type", "actor_id", "outcome", "correlation_id", "occurred_at"],
        {
            "audit_id": ULID, "action": {"type": "string", "minLength": 1, "maxLength": 128},
            "actor_type": {"enum": ["OPERATOR", "SCHEDULER", "ORCHESTRATOR", "RECONCILER", "SYSTEM"]},
            "actor_id": {"type": "string", "maxLength": 128},
            "subject_type": {"oneOf": [{"type": "string"}, {"type": "null"}]}, "subject_id": ULID_N,
            "production_id": ULID_N,
            "outcome": {"enum": ["ALLOWED", "DENIED", "BLOCKED", "APPROVED", "REJECTED", "ERROR"]},
            "policy_decision_id": ULID_N,
            "reason_code": {"oneOf": [{"type": "string", "pattern": "^AMCCA-[A-Z]{2,4}-[0-9]{3}$"}, {"type": "null"}]},
            "correlation_id": ULID, "occurred_at": TS,
        },
        "Who did what, under which authority, with what outcome.")

    # -------------------------------------------------------------- agent-run
    S["agent-run"] = base(
        "agent-run", "Agent invocation record",
        ["run_id", "agent_id", "agent_version", "prompt_version_id", "model_id",
         "state", "correlation_id", "started_at"],
        {
            "run_id": ULID, "production_id": ULID_N, "job_id": ULID_N,
            "agent_id": {"type": "string", "minLength": 1, "maxLength": 64},
            "agent_version": {"type": "string", "pattern": "^[0-9]+\\.[0-9]+\\.[0-9]+$"},
            "prompt_version_id": ULID, "model_id": {"type": "string", "minLength": 1, "maxLength": 128},
            "model_params_hash": SHA,
            "state": {"enum": ["STARTED", "SUCCEEDED", "VALIDATION_FAILED", "FAILED",
                               "BLOCKED", "TIMED_OUT", "UNKNOWN_EXTERNAL_STATE"]},
            "input_hash": SHA, "output_hash": SHA_N,
            "output_valid": {"oneOf": [{"type": "boolean"}, {"type": "null"}]},
            "provider_request_id": {"oneOf": [{"type": "string"}, {"type": "null"}]},
            "cost_event_id": ULID_N, "correlation_id": ULID, "causation_id": ULID_N,
            "started_at": TS, "finished_at": TS_N,
        },
        "One agent invocation. Reproducibility requires agent_version + prompt_version_id + model_id + params hash.")

    # -------------------------------------------------------------- tool-run
    S["tool-run"] = base(
        "tool-run", "Tool invocation record",
        ["run_id", "tool_id", "tool_version", "state", "side_effect_class", "correlation_id", "started_at"],
        {
            "run_id": ULID, "production_id": ULID_N, "job_id": ULID_N, "agent_run_id": ULID_N,
            "tool_id": {"type": "string", "minLength": 1, "maxLength": 64},
            "tool_version": {"type": "string", "pattern": "^[0-9]+\\.[0-9]+\\.[0-9]+$"},
            "side_effect_class": {"enum": ["PURE", "READ", "LOCAL_WRITE", "EXTERNAL_IDEMPOTENT", "EXTERNAL_UNSAFE"]},
            "state": {"enum": ["STARTED", "SUCCEEDED", "FAILED", "BLOCKED", "TIMED_OUT", "UNKNOWN_EXTERNAL_STATE"]},
            "intent_id": ULID_N, "idempotency_key": {"oneOf": [{"type": "string"}, {"type": "null"}]},
            "input_hash": SHA, "output_hash": SHA_N,
            "correlation_id": ULID, "causation_id": ULID_N, "started_at": TS, "finished_at": TS_N,
        },
        "One tool invocation. EXTERNAL_UNSAFE tools must carry intent_id and idempotency_key.")
    S["tool-run"]["allOf"] = [{
        "if": {"properties": {"side_effect_class": {"const": "EXTERNAL_UNSAFE"}}, "required": ["side_effect_class"]},
        "then": {"required": ["intent_id", "idempotency_key"],
                 "properties": {"intent_id": {"type": "string"}, "idempotency_key": {"type": "string"}}},
        "$comment": "Invariant I-03: every external mutation has a persisted intent before the call."
    }]

    # -------------------------------------------------------------- qa
    S["qa"] = base(
        "qa", "QA report",
        ["report_id", "production_id", "artifact_version_id", "stage", "overall_score",
         "critical_scores", "verdict", "findings", "evaluated_at"],
        {
            "report_id": ULID, "production_id": ULID, "artifact_version_id": ULID,
            "stage": {"enum": ["TECHNICAL_QA", "VISUAL_QA", "AUDIO_QA", "CONTENT_QA",
                               "RETENTION_QA", "COMPLIANCE_QA", "SCORING"]},
            "overall_score": {"type": "number", "minimum": 0, "maximum": 10},
            "critical_scores": {
                "type": "object", "additionalProperties": False,
                "required": ["factual_accuracy", "rights", "technical_integrity",
                             "audio_intelligibility", "visual_integrity"],
                "properties": {k: {"type": "number", "minimum": 0, "maximum": 10} for k in
                               ["factual_accuracy", "rights", "technical_integrity",
                                "audio_intelligibility", "visual_integrity"]},
            },
            "verdict": {"enum": ["PASS", "FAIL"]},
            "threshold_profile_id": ULID,
            "findings": {
                "type": "array",
                "items": {
                    "type": "object", "additionalProperties": False,
                    "required": ["check_id", "check_kind", "status", "severity", "responsible_artifact_version_id"],
                    "properties": {
                        "check_id": {"type": "string", "pattern": "^QA-[A-Z]{3}-[0-9]{3}$"},
                        "check_kind": {"enum": ["DETERMINISTIC", "AI_ASSISTED"]},
                        "status": {"enum": ["PASS", "FAIL", "WARN"]},
                        "severity": {"enum": ["INFO", "LOW", "MEDIUM", "HIGH", "CRITICAL"]},
                        "responsible_artifact_version_id": ULID,
                        "remediation_code": {"oneOf": [{"type": "string"}, {"type": "null"}]},
                        "expected": {"oneOf": [{"type": "string"}, {"type": "null"}]},
                        "actual": {"oneOf": [{"type": "string"}, {"type": "null"}]},
                        "scene_ref": {"oneOf": [{"type": "string"}, {"type": "null"}]},
                        "timecode_ms": {"oneOf": [{"type": "integer", "minimum": 0}, {"type": "null"}]},
                        "evidence_ref": {"oneOf": [{"type": "string"}, {"type": "null"}]},
                        "message": {"type": "string", "maxLength": 2000},
                    }
                }
            },
            "evaluated_at": TS,
        },
        "One QA stage result for one artifact version.")

    # -------------------------------------------------------------- claim
    S["claim"] = base(
        "claim", "Research claim",
        ["claim_id", "production_id", "text", "status", "materiality", "sources", "created_at"],
        {
            "claim_id": ULID, "production_id": ULID, "text": {"type": "string", "minLength": 1, "maxLength": 4000},
            "status": {"enum": ["VERIFIED", "DISPUTED", "ESTIMATED", "UNKNOWN"]},
            "materiality": {"enum": ["MATERIAL", "INCIDENTAL"]},
            "subject_class": {"enum": ["GENERAL", "PERSON", "HEALTH", "FINANCE", "LEGAL", "BREAKING_EVENT"]},
            "sources": {
                "type": "array", "minItems": 1,
                "items": {
                    "type": "object", "additionalProperties": False,
                    "required": ["source_id", "url", "retrieved_at", "trust_tier"],
                    "properties": {
                        "source_id": ULID, "url": {"type": "string", "format": "uri"},
                        "retrieved_at": TS, "publisher": {"oneOf": [{"type": "string"}, {"type": "null"}]},
                        "published_at": TS_N, "content_hash": SHA,
                        "trust_tier": {"enum": ["PRIMARY", "SECONDARY", "AGGREGATOR", "UNRATED"]},
                        "excerpt_hash": SHA_N,
                    }
                }
            },
            "contradicted_by": {"type": "array", "items": ULID}, "created_at": TS,
        },
        "Evidence-plane record.")

    # -------------------------------------------------------------- rights
    S["rights"] = base(
        "rights", "Asset rights record",
        ["rights_id", "production_id", "asset_hash", "status", "license", "provenance",
         "commercial_use", "modification", "evaluated_at"],
        {
            "rights_id": ULID, "production_id": ULID, "asset_hash": SHA,
            "status": {"enum": ["GREEN", "YELLOW", "RED"]}, "license": {"type": "string", "minLength": 1, "maxLength": 200},
            "provenance": {"enum": ["GENERATED", "LICENSED_STOCK", "PUBLIC_DOMAIN",
                                    "OPERATOR_SUPPLIED", "OPEN_LICENCE", "UNKNOWN"]},
            "generator_model_id": {"oneOf": [{"type": "string"}, {"type": "null"}]},
            "author": {"oneOf": [{"type": "string"}, {"type": "null"}]},
            "acquired_at": TS_N, "expires_at": TS_N,
            "commercial_use": {"enum": ["ALLOWED", "DENIED", "UNKNOWN"]},
            "modification": {"enum": ["ALLOWED", "DENIED", "UNKNOWN"]},
            "attribution_required": {"type": "boolean"},
            "attribution_text": {"oneOf": [{"type": "string"}, {"type": "null"}]},
            "restrictions": {"type": "array", "items": {"type": "string"}},
            "evidence_ref": {"oneOf": [{"type": "string"}, {"type": "null"}]}, "evaluated_at": TS,
        },
        "Rights status per asset.")

    # ------------------------------------------------------- cost-event (V31-04)
    S["cost-event"] = base(
        "cost-event", "Cost event",
        ["cost_event_id", "production_id", "kind", "amount", "currency",
         "reconciliation_state", "pricing_snapshot_id", "occurred_at"],
        {
            "cost_event_id": ULID, "production_id": ULID_N, "job_id": ULID_N, "agent_run_id": ULID_N,
            "kind": {"enum": ["ESTIMATE", "RESERVATION", "SETTLEMENT", "RELEASE", "ADJUSTMENT"]},
            "amount": {
                **SIGNED_MONEY,
                "description": "SignedMoney (V31-04): ESTIMATE, RESERVATION and SETTLEMENT are always "
                                "non-negative in practice, but ADJUSTMENT is a signed accounting "
                                "correction (e.g. a provider refund or an under-billed correction), so "
                                "the field type must admit a sign. Domain validation, not the schema, "
                                "enforces that non-ADJUSTMENT kinds carry a non-negative value."
            },
            "currency": CUR, "provider": {"oneOf": [{"type": "string"}, {"type": "null"}]},
            "model_id": {"oneOf": [{"type": "string"}, {"type": "null"}]},
            "provider_request_id": {"oneOf": [{"type": "string"}, {"type": "null"}]},
            "units": {"oneOf": [{"type": "object"}, {"type": "null"}]},
            "pricing_snapshot_id": ULID,
            "reconciliation_state": {"enum": ["ESTIMATED", "RECONCILED", "ESTIMATED_UNRECONCILED", "DISPUTED"]},
            "budget_id": ULID_N, "occurred_at": TS,
        },
        "Every euro that moves.")
    S["cost-event"]["allOf"] = [{
        "if": {"properties": {"kind": {"enum": ["ESTIMATE", "RESERVATION", "SETTLEMENT"]}}, "required": ["kind"]},
        "then": {"properties": {"amount": NONNEG_MONEY}},
        "$comment": "V31-04: only ADJUSTMENT (and RELEASE, structurally a reduction represented as its "
                    "own non-negative magnitude) may be negative; the common kinds cannot."
    }]

    # -------------------------------------------------------------- analytics
    S["analytics"] = base(
        "analytics", "Analytics observation",
        ["observation_id", "production_id", "publication_id", "metric", "value", "provenance", "observed_at"],
        {
            "observation_id": ULID, "production_id": ULID, "publication_id": ULID,
            "metric": {"type": "string", "minLength": 1, "maxLength": 64}, "value": {"type": "number"},
            "unit": {"oneOf": [{"type": "string"}, {"type": "null"}]},
            "currency": {"oneOf": [CUR, {"type": "null"}]},
            "provenance": {"enum": ["API_MEASURED", "IMPORTED", "ESTIMATED", "UNAVAILABLE"]},
            "window_start": TS_N, "window_end": TS_N, "source_account_id": ULID_N, "observed_at": TS,
        },
        "One measured or estimated metric.")

    # ---------------------------------------------------- referral (V31-09 alignment)
    S["referral"] = base(
        "referral", "Referral link",
        ["referral_id", "program_id", "state", "validation_method", "validated_at",
         "disclosure_required", "created_at"],
        {
            "referral_id": ULID, "program_id": ULID, "production_id": ULID_N,
            "brand": {"type": "string", "maxLength": 200},
            "code": {"oneOf": [{"type": "string"}, {"type": "null"}]},
            "url": {"oneOf": [{"type": "string", "format": "uri"}, {"type": "null"}]},
            "state": {"enum": ["ACTIVE", "EXPIRED", "BLOCKED", "REVIEW", "UNVERIFIED", "DISCOVERED"]},
            "validation_method": {"enum": ["OFFICIAL_API", "OFFICIAL_DASHBOARD", "OPERATOR_VERIFIED",
                                           "HTTP_CHECK", "MANUAL_CONFIRMATION"]},
            "validation_evidence_ref": {"oneOf": [{"type": "string"}, {"type": "null"}]},
            "validated_at": TS, "expires_at": TS_N,
            "commission_model": {"oneOf": [{"type": "string"}, {"type": "null"}]},
            "geo_restrictions": {"type": "array", "items": {"type": "string", "pattern": "^[A-Z]{2}$"}},
            "platform_restrictions": {"type": "array", "items": {"type": "string"}},
            "disclosure_required": {"type": "boolean"}, "created_at": TS,
        },
        "A referral usable in content.")
    S["referral"]["allOf"] = [{
        "if": {"properties": {"state": {"const": "ACTIVE"}}, "required": ["state"]},
        "then": {"properties": {"validation_method": {"enum": ["OFFICIAL_API", "OFFICIAL_DASHBOARD",
                                                               "OPERATOR_VERIFIED", "MANUAL_CONFIRMATION"]}},
                 "required": ["validation_evidence_ref"]},
        "$comment": "AFFILIATE_POLICY: a 200 OK is not proof of validity. HTTP_CHECK cannot sustain ACTIVE."
    }]

    # -------------------------------------------------------------- manifest
    S["manifest"] = base(
        "manifest", "Production artifact manifest",
        ["manifest_id", "production_id", "sealed", "artifacts", "created_at"],
        {
            "manifest_id": ULID, "production_id": ULID,
            "sealed": {"type": "boolean"},
            "artifacts": {
                "type": "array", "minItems": 1,
                "items": {
                    "type": "object", "additionalProperties": False,
                    "required": ["artifact_version_id", "artifact_kind", "sha256", "bytes", "state"],
                    "properties": {
                        "artifact_version_id": ULID,
                        "artifact_kind": {"enum": ["RESEARCH", "SCRIPT", "STORYBOARD", "IMAGE", "VIDEO_CLIP",
                                                   "VOICE", "MUSIC", "CAPTION", "RENDER", "THUMBNAIL", "METADATA"]},
                        "sha256": SHA, "bytes": {"type": "integer", "minimum": 0}, "path": {"type": "string"},
                        "state": {"enum": ["CURRENT", "SUPERSEDED", "INVALIDATED", "TOMBSTONED"]},
                        "depends_on": {"type": "array", "items": ULID},
                        "generator_model_id": {"oneOf": [{"type": "string"}, {"type": "null"}]},
                        "rights_id": ULID_N,
                    }
                }
            },
            "created_at": TS,
        },
        "The hash-verifiable inventory of everything a production is made of.")

    # -------------------------------------------------------- config (V31-04)
    S["config"] = {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$id": f"amcca://schema/config/{SV}",
        "title": "AMCCA configuration",
        "description": "Validated at startup by the preflight (SPEC/49).",
        "type": "object", "additionalProperties": False,
        "required": ["schema_version", "environment", "autonomy_mode", "publishing_enabled",
                     "dry_run", "data_root", "budgets", "storage", "logging", "providers", "platforms"],
        "properties": {
            "schema_version": {"const": SV},
            "environment": {"enum": ["DEVELOPMENT", "STAGING", "PRODUCTION"]},
            "autonomy_mode": {"enum": ["MANUAL", "ASSISTED", "AUTONOMOUS"]},
            "publishing_enabled": {"type": "boolean"}, "dry_run": {"type": "boolean"},
            "data_root": {"type": "string", "minLength": 1}, "currency": CUR,
            "logging": {
                "type": "object", "additionalProperties": False, "required": ["level"],
                "properties": {"level": {"enum": ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"]},
                               "retention_days": {"type": "integer", "minimum": 1, "maximum": 3650}}
            },
            "budgets": {
                "type": "object", "additionalProperties": False,
                "required": ["per_production", "per_rework", "per_recovery", "daily", "monthly",
                             "warn_percent", "pause_percent", "block_percent"],
                "properties": {
                    "per_production": {**NONNEG_MONEY, "description": "A budget limit cannot be negative (V31-04)."},
                    "per_rework": NONNEG_MONEY, "per_recovery": NONNEG_MONEY,
                    "daily": NONNEG_MONEY, "monthly": NONNEG_MONEY,
                    "warn_percent": {"type": "integer", "minimum": 1, "maximum": 100},
                    "pause_percent": {"type": "integer", "minimum": 1, "maximum": 100},
                    "block_percent": {"type": "integer", "minimum": 1, "maximum": 100},
                },
            },
            "storage": {
                "type": "object", "additionalProperties": False,
                "required": ["minimum_free_gb", "temp_retention_hours", "cache_retention_days"],
                "properties": {"minimum_free_gb": {"type": "integer", "minimum": 1},
                               "temp_retention_hours": {"type": "integer", "minimum": 1},
                               "cache_retention_days": {"type": "integer", "minimum": 1}}
            },
            "providers": {
                "type": "object", "additionalProperties": False, "required": ["gateway"],
                "properties": {"gateway": {
                    "type": "object", "additionalProperties": False,
                    "required": ["id", "enabled", "base_url", "api_key_secret_ref",
                                 "timeout_seconds", "capabilities_verified"],
                    "properties": {
                        "id": {"type": "string"}, "enabled": {"type": "boolean"},
                        "base_url": {"type": "string", "format": "uri", "pattern": "^https://"},
                        "api_key_secret_ref": {"type": "string", "pattern": "^secret://"},
                        "timeout_seconds": {"type": "integer", "minimum": 1, "maximum": 900},
                        "capabilities_verified": {"type": "boolean"},
                        # D-034: model token prices are operator-supplied here, materialised into
                        # pricing_snapshots, and are the only source AgentRuntime will price a model
                        # call against. Prices are external and volatile (SPEC/21), so each entry
                        # carries its own retrieved_at + source_ref; a cost cannot be computed
                        # against a price lacking either. Optional: with no entry for a model, an
                        # agent run still completes but its cost_events row is ESTIMATED_UNRECONCILED.
                        "model_pricing": {
                            "type": "array",
                            "items": {
                                "type": "object", "additionalProperties": False,
                                "required": ["model_id", "input_per_1m_tokens",
                                             "output_per_1m_tokens", "currency",
                                             "retrieved_at", "source_ref"],
                                "properties": {
                                    "model_id": {"type": "string", "minLength": 1, "maxLength": 128},
                                    "input_per_1m_tokens": NONNEG_MONEY,
                                    "output_per_1m_tokens": NONNEG_MONEY,
                                    "currency": CUR,
                                    "effective_at": {"type": "string", "format": "date-time"},
                                    "retrieved_at": {"type": "string", "format": "date-time"},
                                    "source_ref": {"type": "string", "minLength": 1},
                                },
                            },
                        },
                    }
                }}
            },
            "platforms": {
                "type": "object",
                "additionalProperties": {
                    "type": "object", "additionalProperties": False, "required": ["enabled"],
                    "properties": {"enabled": {"type": "boolean"},
                                   "synthetic_label_required": {"type": "boolean"},
                                   "capabilities": {"type": "array", "items": {"type": "string"}}}
                }
            },
            "policy": {
                "type": "object", "additionalProperties": False,
                "properties": {
                    "research": {"type": "object", "additionalProperties": False,
                                 "properties": {"min_sources": {"type": "integer", "minimum": 1},
                                                "max_attempts": {"type": "integer", "minimum": 1}}},
                    "qa": {"type": "object", "additionalProperties": False,
                           "properties": {"overall_min": {"type": "number", "minimum": 0, "maximum": 10},
                                          "critical_min": {"type": "number", "minimum": 0, "maximum": 10}}},
                    "rework": {"type": "object", "additionalProperties": False,
                               "properties": {"max_attempts": {"type": "integer", "minimum": 0},
                                              "min_severity": {"enum": ["LOW", "MEDIUM", "HIGH", "CRITICAL"]}}},
                    "reconcile": {"type": "object", "additionalProperties": False,
                                  "properties": {"max_attempts": {"type": "integer", "minimum": 1},
                                                 "interval_seconds": {"type": "integer", "minimum": 1}}},
                }
            }
        }
    }
    return S


# ===================================================================
# 3. Database (canonical model) — see original for full family list;
#    only the families touched by V3.1 are redefined here, the rest
#    are ported verbatim from the V3 model.
# ===================================================================

def build_tables_and_doc():
    ID = "`id` TEXT PK (ULID)"
    CT = "`created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL"

    MODEL = collections.OrderedDict()

    MODEL["Migration and settings"] = [
     ("schema_migrations",
      "`version` INTEGER PK, `name` TEXT NOT NULL, `checksum` TEXT NOT NULL, `applied_at` TEXT NOT NULL, `applied_by` TEXT NOT NULL, `rollback_sql_ref` TEXT NULL",
      "PK(version). UNIQUE(name). Applying a migration whose recorded checksum differs from the shipped file aborts startup with `AMCCA-DB-002`.",
      "none (small table, PK scan)"),
     ("settings",
      "`key` TEXT PK, `value_json` TEXT NOT NULL, `schema_version` TEXT NOT NULL, `is_secret_ref` INTEGER NOT NULL DEFAULT 0, `updated_at` TEXT NOT NULL, `updated_by` TEXT NOT NULL",
      "PK(key). CHECK(is_secret_ref IN (0,1)).", "none"),
     ("kill_switch_state",
      "`id` INTEGER PK CHECK(id=1), `mode` TEXT NOT NULL, `engaged_at` TEXT NULL, `engaged_by` TEXT NULL, `reason` TEXT NULL, `cleared_at` TEXT NULL, `cleared_by` TEXT NULL",
      "Single-row table. CHECK(mode IN ('NORMAL','PAUSED','PUBLISHING_DISABLED','EMERGENCY_STOP')).", "none"),
    ]

    MODEL["Production core"] = [
     ("productions",
      f"{ID}, `state` TEXT NOT NULL, `blocked_from` TEXT NULL, `unknown_from` TEXT NULL, `rework_attempts` INTEGER NOT NULL DEFAULT 0, `aggregate_version` INTEGER NOT NULL DEFAULT 0, `autonomy_mode` TEXT NOT NULL, `title` TEXT NULL, `language` TEXT NOT NULL, `niche_id` TEXT NULL, `opportunity_id` TEXT NULL, `current_manifest_id` TEXT NULL, `schema_version` TEXT NOT NULL, {CT}",
      "FK(niche_id)->niches, FK(opportunity_id)->opportunities, FK(current_manifest_id)->artifact_manifests. CHECK(state IN <32 canonical states>). CHECK(state<>'BLOCKED' OR blocked_from IS NOT NULL). CHECK(state<>'UNKNOWN_EXTERNAL_STATE' OR unknown_from IS NOT NULL).",
      "IX(state), IX(updated_at), IX(niche_id)"),
     ("production_versions",
      f"{ID}, `production_id` TEXT NOT NULL, `version_no` INTEGER NOT NULL, `manifest_id` TEXT NOT NULL, `reason` TEXT NOT NULL, `created_at` TEXT NOT NULL",
      "FK(production_id)->productions ON DELETE RESTRICT. UNIQUE(production_id, version_no).",
      "IX(production_id, version_no)"),
     ("state_transitions",
      f"{ID}, `production_id` TEXT NOT NULL, `transition_id` TEXT NOT NULL, `from_state` TEXT NOT NULL, `to_state` TEXT NOT NULL, `event_id` TEXT NOT NULL, `actor_type` TEXT NOT NULL, `correlation_id` TEXT NOT NULL, `occurred_at` TEXT NOT NULL",
      "FK(production_id)->productions, FK(event_id)->events. UNIQUE(event_id). `transition_id` MUST match an id in the canonical state model; a value outside that set is rejected with `AMCCA-STM-001`.",
      "IX(production_id, occurred_at), IX(transition_id)"),
    ]

    MODEL["Artifacts and lineage"] = [
     ("artifacts",
      f"{ID}, `production_id` TEXT NOT NULL, `kind` TEXT NOT NULL, `current_version_id` TEXT NULL, {CT}",
      "FK(production_id)->productions.", "IX(production_id, kind)"),
     ("artifact_versions",
      f"{ID}, `artifact_id` TEXT NOT NULL, `version_no` INTEGER NOT NULL, `sha256` TEXT NOT NULL, `bytes` INTEGER NOT NULL, `rel_path` TEXT NOT NULL, `state` TEXT NOT NULL, `generator_model_id` TEXT NULL, `prompt_version_id` TEXT NULL, `rights_id` TEXT NULL, `created_at` TEXT NOT NULL",
      "FK(artifact_id)->artifacts. UNIQUE(artifact_id, version_no). CHECK(state IN ('CURRENT','SUPERSEDED','INVALIDATED','TOMBSTONED')). CHECK(length(sha256)=64).",
      "UX(artifact_id, version_no), IX(sha256), IX(state)"),
     ("artifact_edges",
      "`parent_version_id` TEXT NOT NULL, `child_version_id` TEXT NOT NULL, `edge_kind` TEXT NOT NULL, `created_at` TEXT NOT NULL",
      "PK(parent_version_id, child_version_id). Both FKs -> artifact_versions.", "IX(child_version_id)"),
     ("artifact_manifests",
      f"{ID}, `production_id` TEXT NOT NULL, `sealed` INTEGER NOT NULL DEFAULT 0, `manifest_sha256` TEXT NOT NULL, `schema_version` TEXT NOT NULL, `created_at` TEXT NOT NULL",
      "FK(production_id)->productions. CHECK(sealed IN (0,1)).", "IX(production_id, created_at)"),
    ]

    MODEL["Durable execution"] = [
     ("jobs",
      f"{ID}, `production_id` TEXT NULL, `type` TEXT NOT NULL, `state` TEXT NOT NULL, `priority` INTEGER NOT NULL, `idempotency_key` TEXT NOT NULL, `attempt` INTEGER NOT NULL DEFAULT 0, `max_attempts` INTEGER NOT NULL, `scheduled_at` TEXT NULL, `deadline_at` TEXT NULL, `estimated_cost` TEXT NULL, `reserved_cost` TEXT NULL, `currency` TEXT NOT NULL, `correlation_id` TEXT NOT NULL, `causation_id` TEXT NULL, `last_error_code` TEXT NULL, `payload_json` TEXT NOT NULL, `schema_version` TEXT NOT NULL, {CT}",
      "UNIQUE(idempotency_key). CHECK(priority BETWEEN 0 AND 5). CHECK(state IN ('QUEUED','LEASED','RUNNING','SUCCEEDED','FAILED','BLOCKED','UNKNOWN_EXTERNAL_STATE','CANCELLED','DEAD_LETTER')). CHECK(estimated_cost IS NULL OR estimated_cost NOT LIKE '-%') and CHECK(reserved_cost IS NULL OR reserved_cost NOT LIKE '-%') — non-negative money enforced in storage too (V31-04).",
      "IX(state, priority, scheduled_at), IX(production_id), UX(idempotency_key)"),
     ("job_attempts",
      f"{ID}, `job_id` TEXT NOT NULL, `attempt_no` INTEGER NOT NULL, `worker_id` TEXT NOT NULL, `outcome` TEXT NOT NULL, `error_code` TEXT NULL, `started_at` TEXT NOT NULL, `finished_at` TEXT NULL",
      "FK(job_id)->jobs. UNIQUE(job_id, attempt_no).", "IX(job_id, attempt_no)"),
     ("leases",
      "`job_id` TEXT PK, `owner_id` TEXT NOT NULL, `acquired_at` TEXT NOT NULL, `lease_until` TEXT NOT NULL, `heartbeat_at` TEXT NOT NULL, `fence_token` INTEGER NOT NULL",
      "PK(job_id). FK(job_id)->jobs ON DELETE CASCADE. `fence_token` monotonically increases per acquisition.",
      "IX(lease_until)"),
     ("intents",
      f"{ID}, `job_id` TEXT NULL, `production_id` TEXT NULL, `kind` TEXT NOT NULL, `target` TEXT NOT NULL, `idempotency_key` TEXT NOT NULL, `request_fingerprint` TEXT NOT NULL, `state` TEXT NOT NULL, `external_request_id` TEXT NULL, `attempt_count` INTEGER NOT NULL DEFAULT 0, `dispatched_at` TEXT NULL, `resolved_at` TEXT NULL, {CT}",
      "UNIQUE(idempotency_key). CHECK(state IN ('CREATED','DISPATCHED','CONFIRMED','REFUTED','UNKNOWN','ABANDONED')).",
      "IX(state), UX(idempotency_key), IX(production_id)"),
     ("reconciliation_attempts",
      f"{ID}, `intent_id` TEXT NOT NULL, `attempt_no` INTEGER NOT NULL, `method` TEXT NOT NULL, `outcome` TEXT NOT NULL, `evidence_ref` TEXT NULL, `occurred_at` TEXT NOT NULL",
      "FK(intent_id)->intents. UNIQUE(intent_id, attempt_no). CHECK(outcome IN ('CONFIRMED','REFUTED','INCONCLUSIVE')).",
      "IX(intent_id)"),
    ]

    MODEL["Events and audit"] = [
     ("events",
      "`event_id` TEXT PK, `event_type` TEXT NOT NULL, `aggregate_type` TEXT NOT NULL, `aggregate_id` TEXT NOT NULL, `aggregate_version` INTEGER NOT NULL, `correlation_id` TEXT NOT NULL, `causation_id` TEXT NULL, `transition_id` TEXT NULL, `payload_json` TEXT NOT NULL, `schema_version` TEXT NOT NULL, `occurred_at` TEXT NOT NULL, `seq` INTEGER NOT NULL",
      "PK(event_id). UNIQUE(aggregate_type, aggregate_id, aggregate_version).",
      "IX(aggregate_type, aggregate_id, aggregate_version), IX(correlation_id), IX(occurred_at), IX(seq)"),
     ("audit_log",
      "`audit_id` TEXT PK, `action` TEXT NOT NULL, `actor_type` TEXT NOT NULL, `actor_id` TEXT NOT NULL, `subject_type` TEXT NULL, `subject_id` TEXT NULL, `production_id` TEXT NULL, `outcome` TEXT NOT NULL, `policy_decision_id` TEXT NULL, `reason_code` TEXT NULL, `correlation_id` TEXT NOT NULL, `schema_version` TEXT NOT NULL, `occurred_at` TEXT NOT NULL",
      "PK(audit_id). Physically separate from `events` (D-018).",
      "IX(occurred_at), IX(production_id), IX(correlation_id)"),
    ]

    MODEL["Agents, tools, prompts"] = [
     ("agent_runs",
      "`run_id` TEXT PK, `production_id` TEXT NULL, `job_id` TEXT NULL, `agent_id` TEXT NOT NULL, `agent_version` TEXT NOT NULL, `prompt_version_id` TEXT NOT NULL, `model_id` TEXT NOT NULL, `model_params_hash` TEXT NOT NULL, `state` TEXT NOT NULL, `input_hash` TEXT NOT NULL, `output_hash` TEXT NULL, `output_valid` INTEGER NULL, `provider_request_id` TEXT NULL, `cost_event_id` TEXT NULL, `correlation_id` TEXT NOT NULL, `causation_id` TEXT NULL, `schema_version` TEXT NOT NULL, `started_at` TEXT NOT NULL, `finished_at` TEXT NULL",
      "FK(prompt_version_id)->prompt_versions.", "IX(production_id), IX(agent_id, started_at), IX(provider_request_id)"),
     ("tool_runs",
      "`run_id` TEXT PK, `production_id` TEXT NULL, `job_id` TEXT NULL, `agent_run_id` TEXT NULL, `tool_id` TEXT NOT NULL, `tool_version` TEXT NOT NULL, `side_effect_class` TEXT NOT NULL, `state` TEXT NOT NULL, `intent_id` TEXT NULL, `idempotency_key` TEXT NULL, `input_hash` TEXT NOT NULL, `output_hash` TEXT NULL, `correlation_id` TEXT NOT NULL, `causation_id` TEXT NULL, `schema_version` TEXT NOT NULL, `started_at` TEXT NOT NULL, `finished_at` TEXT NULL",
      "FK(intent_id)->intents. CHECK(side_effect_class<>'EXTERNAL_UNSAFE' OR intent_id IS NOT NULL) — invariant I-03 enforced in storage.",
      "IX(production_id), IX(tool_id, started_at)"),
     ("prompt_templates",
      f"{ID}, `key` TEXT NOT NULL, `purpose` TEXT NOT NULL, `current_version_id` TEXT NULL, {CT}",
      "UNIQUE(key).", "UX(key)"),
     ("prompt_versions",
      f"{ID}, `template_id` TEXT NOT NULL, `version_no` INTEGER NOT NULL, `body_sha256` TEXT NOT NULL, `body_ref` TEXT NOT NULL, `notes` TEXT NULL, `created_at` TEXT NOT NULL",
      "FK(template_id)->prompt_templates. UNIQUE(template_id, version_no).", "UX(template_id, version_no), IX(body_sha256)"),
     ("agent_contracts",
      f"{ID}, `agent_id` TEXT NOT NULL, `agent_version` TEXT NOT NULL, `input_schema_ref` TEXT NOT NULL, `output_schema_ref` TEXT NOT NULL, `allowed_tools_json` TEXT NOT NULL, `forbidden_tools_json` TEXT NOT NULL, `timeout_seconds` INTEGER NOT NULL, `max_cost` TEXT NOT NULL, `max_autonomy` TEXT NOT NULL, `created_at` TEXT NOT NULL",
      "UNIQUE(agent_id, agent_version). `max_autonomy` caps the agent regardless of system autonomy mode.",
      "UX(agent_id, agent_version)"),
    ]

    MODEL["Evidence plane"] = [
     ("sources",
      f"{ID}, `url` TEXT NOT NULL, `publisher` TEXT NULL, `published_at` TEXT NULL, `retrieved_at` TEXT NOT NULL, `content_hash` TEXT NOT NULL, `trust_tier` TEXT NOT NULL, `robots_allowed` INTEGER NOT NULL, `created_at` TEXT NOT NULL",
      "UNIQUE(url, content_hash). CHECK(trust_tier IN ('PRIMARY','SECONDARY','AGGREGATOR','UNRATED')).", "IX(retrieved_at), UX(url, content_hash)"),
     ("claims",
      f"{ID}, `production_id` TEXT NOT NULL, `text` TEXT NOT NULL, `status` TEXT NOT NULL, `materiality` TEXT NOT NULL, `subject_class` TEXT NOT NULL, `contains_personal_data` INTEGER NOT NULL DEFAULT 0, `schema_version` TEXT NOT NULL, `created_at` TEXT NOT NULL",
      "FK(production_id)->productions. CHECK(status IN ('VERIFIED','DISPUTED','ESTIMATED','UNKNOWN')).",
      "IX(production_id, status), IX(contains_personal_data)"),
     ("claim_sources",
      "`claim_id` TEXT NOT NULL, `source_id` TEXT NOT NULL, `relation` TEXT NOT NULL, `excerpt_hash` TEXT NULL",
      "PK(claim_id, source_id). CHECK(relation IN ('SUPPORTS','CONTRADICTS','CONTEXT')).", "IX(source_id)"),
     ("rights_records",
      f"{ID}, `production_id` TEXT NOT NULL, `asset_hash` TEXT NOT NULL, `status` TEXT NOT NULL, `license` TEXT NOT NULL, `provenance` TEXT NOT NULL, `generator_model_id` TEXT NULL, `author` TEXT NULL, `acquired_at` TEXT NULL, `expires_at` TEXT NULL, `commercial_use` TEXT NOT NULL, `modification` TEXT NOT NULL, `attribution_required` INTEGER NOT NULL, `attribution_text` TEXT NULL, `restrictions_json` TEXT NOT NULL, `evidence_ref` TEXT NULL, `schema_version` TEXT NOT NULL, `evaluated_at` TEXT NOT NULL",
      "FK(production_id)->productions. CHECK(status IN ('GREEN','YELLOW','RED')). CHECK(status<>'GREEN' OR (commercial_use='ALLOWED' AND modification<>'UNKNOWN')).",
      "IX(asset_hash), IX(production_id, status)"),
     ("qa_reports",
      "`report_id` TEXT PK, `production_id` TEXT NOT NULL, `artifact_version_id` TEXT NOT NULL, `stage` TEXT NOT NULL, `overall_score` REAL NOT NULL, `critical_scores_json` TEXT NOT NULL, `verdict` TEXT NOT NULL, `threshold_profile_id` TEXT NOT NULL, `schema_version` TEXT NOT NULL, `evaluated_at` TEXT NOT NULL",
      "FK(production_id)->productions, FK(artifact_version_id)->artifact_versions. CHECK(verdict IN ('PASS','FAIL')).",
      "IX(production_id, stage), IX(artifact_version_id)"),
     ("qa_findings",
      f"{ID}, `report_id` TEXT NOT NULL, `check_id` TEXT NOT NULL, `check_kind` TEXT NOT NULL, `status` TEXT NOT NULL, `severity` TEXT NOT NULL, `responsible_artifact_version_id` TEXT NOT NULL, `remediation_code` TEXT NULL, `expected` TEXT NULL, `actual` TEXT NULL, `scene_ref` TEXT NULL, `timecode_ms` INTEGER NULL, `evidence_ref` TEXT NULL, `message` TEXT NULL",
      "FK(report_id)->qa_reports ON DELETE CASCADE, FK(responsible_artifact_version_id)->artifact_versions. CHECK(check_kind IN ('DETERMINISTIC','AI_ASSISTED')).",
      "IX(report_id), IX(responsible_artifact_version_id), IX(severity)"),
    ]

    MODEL["Opportunity and strategy"] = [
     ("niches",
      f"{ID}, `name` TEXT NOT NULL, `language` TEXT NOT NULL, `state` TEXT NOT NULL, `evidence_ref` TEXT NULL, {CT}",
      "UNIQUE(name, language). CHECK(state IN ('CANDIDATE','TESTING','PROVEN','RETIRED')).", "UX(name, language), IX(state)"),
     ("trends",
      f"{ID}, `niche_id` TEXT NULL, `label` TEXT NOT NULL, `signal_strength` REAL NOT NULL, `observed_at` TEXT NOT NULL, `source_id` TEXT NOT NULL, `expires_at` TEXT NULL",
      "FK(source_id)->sources.", "IX(niche_id, observed_at), IX(expires_at)"),
     ("opportunities",
      f"{ID}, `niche_id` TEXT NOT NULL, `state` TEXT NOT NULL, `score` REAL NOT NULL, `score_breakdown_json` TEXT NOT NULL, `expected_revenue` TEXT NOT NULL, `expected_cost` TEXT NOT NULL, `risk_penalty` REAL NOT NULL, `currency` TEXT NOT NULL, `scored_at` TEXT NOT NULL, {CT}",
      "FK(niche_id)->niches. CHECK(state IN ('NEW','SCORED','SELECTED','REJECTED','EXPIRED')). CHECK(expected_revenue NOT LIKE '-%') and CHECK(expected_cost NOT LIKE '-%') — non-negative money (V31-04).",
      "IX(state, score), IX(niche_id)"),
     ("hooks",
      f"{ID}, `production_id` TEXT NULL, `text` TEXT NOT NULL, `pattern_id` TEXT NULL, `measured_retention` REAL NULL, `created_at` TEXT NOT NULL",
      "FK(production_id)->productions.", "IX(pattern_id)"),
    ]

    MODEL["Distribution"] = [
     ("platform_accounts",
      f"{ID}, `platform` TEXT NOT NULL, `handle` TEXT NOT NULL, `state` TEXT NOT NULL, `credential_secret_ref` TEXT NOT NULL, `scopes_json` TEXT NOT NULL, `connected_at` TEXT NULL, `last_verified_at` TEXT NULL, {CT}",
      "UNIQUE(platform, handle). CHECK(state IN ('DISCONNECTED','CONNECTED','REAUTH_REQUIRED','SUSPENDED','DISABLED')). CHECK(credential_secret_ref LIKE 'secret://%').",
      "UX(platform, handle), IX(state)"),
     ("platform_capabilities",
      "`platform` TEXT NOT NULL, `account_id` TEXT NOT NULL, `capability` TEXT NOT NULL, `status` TEXT NOT NULL, `evidence_source` TEXT NOT NULL, `verified_at` TEXT NOT NULL, `expires_at` TEXT NULL",
      f"PK(platform, account_id, capability). CHECK(status IN ({', '.join(repr(s) for s in PLATFORM_CAPABILITY_STATUS)})). "
      f"CHECK(status<>'VERIFIED' OR evidence_source IN "
      f"({', '.join(repr(s) for s in PLATFORM_CAPABILITY_AUTHORITATIVE_EVIDENCE)})) "
      "— a secondary source (blog, agency article, community guide) can only ever produce DISCOVERED, never VERIFIED (V31-09).",
      "IX(account_id), IX(expires_at)"),
     ("publications",
      f"{ID}, `production_id` TEXT NOT NULL, `platform` TEXT NOT NULL, `account_id` TEXT NOT NULL, `content_version_id` TEXT NOT NULL, `metadata_version_id` TEXT NULL, `referral_version_id` TEXT NULL, `synthetic_declaration_id` TEXT NULL, `platform_label_required` INTEGER NOT NULL DEFAULT 0, `state` TEXT NOT NULL, `required` INTEGER NOT NULL DEFAULT 1, `idempotency_key` TEXT NOT NULL, `external_id` TEXT NULL, `external_url` TEXT NULL, `evidence_source` TEXT NULL, `evidence_retrieved_at` TEXT NULL, `synthetic_label_applied` INTEGER NOT NULL DEFAULT 0, `attempt_count` INTEGER NOT NULL DEFAULT 0, `last_error_code` TEXT NULL, `schema_version` TEXT NOT NULL, {CT}",
      "FK(production_id)->productions, FK(account_id)->platform_accounts, FK(content_version_id)->artifact_versions, "
      "FK(synthetic_declaration_id)->synthetic_declarations (V31-07). UNIQUE(idempotency_key). "
      "UNIQUE(production_id, platform, account_id, content_version_id). "
      "CHECK(platform_label_required=0 OR synthetic_declaration_id IS NOT NULL) — a required label must be traced to its declaration. "
      "CHECK(state<>'VERIFIED' OR (external_id IS NOT NULL AND evidence_source IN ('OFFICIAL_API','OFFICIAL_DASHBOARD','OPERATOR_CONFIRMATION') AND evidence_retrieved_at IS NOT NULL)) "
      "— invariant I-11 tightened: POST_PUBLISH_CHECK (a resolving URL) cannot satisfy this CHECK (V31-06). "
      "CHECK(state<>'VERIFIED' OR platform_label_required=0 OR synthetic_label_applied=1) — invariant I-18 made structural (V31-07).",
      "UX(idempotency_key), UX(production_id, platform, account_id, content_version_id), IX(state), IX(synthetic_declaration_id)"),
     ("publication_intents",
      f"{ID}, `publication_id` TEXT NOT NULL, `intent_id` TEXT NOT NULL, `sequence_no` INTEGER NOT NULL, `created_at` TEXT NOT NULL",
      "FK(publication_id)->publications, FK(intent_id)->intents. UNIQUE(intent_id). UNIQUE(publication_id, sequence_no).",
      "IX(publication_id)"),
     ("publication_attempts",
      f"{ID}, `publication_id` TEXT NOT NULL, `attempt_no` INTEGER NOT NULL, `outcome` TEXT NOT NULL, `http_status` INTEGER NULL, `provider_request_id` TEXT NULL, `error_code` TEXT NULL, `started_at` TEXT NOT NULL, `finished_at` TEXT NULL",
      "FK(publication_id)->publications. UNIQUE(publication_id, attempt_no). CHECK(outcome IN ('ACCEPTED','REJECTED','ERROR','UNKNOWN')). "
      "An `http_status` of 200 recorded here does not by itself justify any state change (V31-06).",
      "IX(publication_id)"),
     ("synthetic_declarations",
      f"{ID}, `production_id` TEXT NOT NULL, `publication_id` TEXT NULL, `generated_components_json` TEXT NOT NULL, `responsibility_json` TEXT NOT NULL, `platform_label_required` INTEGER NOT NULL, `platform_label_applied` INTEGER NOT NULL DEFAULT 0, `in_content_disclosure_text` TEXT NULL, `policy_basis` TEXT NOT NULL, `evaluated_at` TEXT NOT NULL",
      "FK(production_id)->productions, FK(publication_id)->publications. "
      "`responsibility_json` records which obligation (provider machine-readable marking / deployer disclosure / platform-native label / C2PA provenance) is whose responsibility, per the matrix in SPEC/45 (V31-08). "
      "CHECK(platform_label_required=0 OR platform_label_applied=1 OR publication_id IS NULL).",
      "IX(production_id)"),
    ]

    MODEL["Money"] = [
     ("budgets",
      f"{ID}, `scope` TEXT NOT NULL, `window_start` TEXT NOT NULL, `window_end` TEXT NOT NULL, `limit_amount` TEXT NOT NULL, `currency` TEXT NOT NULL, `state` TEXT NOT NULL, {CT}",
      "UNIQUE(scope, window_start). CHECK(scope IN ('PRODUCTION','REWORK','RECOVERY','DAILY','MONTHLY')). "
      "CHECK(limit_amount NOT LIKE '-%') — a budget limit is NonNegativeMoney, never signed (V31-04).",
      "UX(scope, window_start), IX(state)"),
     ("budget_reservations",
      f"{ID}, `budget_id` TEXT NOT NULL, `production_id` TEXT NULL, `job_id` TEXT NULL, `amount` TEXT NOT NULL, `state` TEXT NOT NULL, `expires_at` TEXT NOT NULL, {CT}",
      "FK(budget_id)->budgets. CHECK(state IN ('HELD','SETTLED','RELEASED','EXPIRED')). CHECK(amount NOT LIKE '-%').",
      "IX(budget_id, state), IX(expires_at)"),
     ("cost_events",
      "`cost_event_id` TEXT PK, `production_id` TEXT NULL, `job_id` TEXT NULL, `agent_run_id` TEXT NULL, `kind` TEXT NOT NULL, `amount` TEXT NOT NULL, `currency` TEXT NOT NULL, `provider` TEXT NULL, `model_id` TEXT NULL, `provider_request_id` TEXT NULL, `units_json` TEXT NULL, `pricing_snapshot_id` TEXT NOT NULL, `reconciliation_state` TEXT NOT NULL, `budget_id` TEXT NULL, `schema_version` TEXT NOT NULL, `occurred_at` TEXT NOT NULL",
      "FK(pricing_snapshot_id)->pricing_snapshots. CHECK(kind IN ('ESTIMATE','RESERVATION','SETTLEMENT','RELEASE','ADJUSTMENT')). "
      "CHECK(kind = 'ADJUSTMENT' OR amount NOT LIKE '-%') — only ADJUSTMENT may be signed (V31-04).",
      "IX(production_id), IX(reconciliation_state), IX(provider_request_id)"),
     ("pricing_snapshots",
      f"{ID}, `provider` TEXT NOT NULL, `model_id` TEXT NOT NULL, `unit` TEXT NOT NULL, `unit_price` TEXT NOT NULL, `currency` TEXT NOT NULL, `effective_at` TEXT NOT NULL, `retrieved_at` TEXT NOT NULL, `source_ref` TEXT NOT NULL, `created_at` TEXT NOT NULL",
      "UNIQUE(provider, model_id, unit, effective_at). Immutable.", "UX(provider, model_id, unit, effective_at)"),
     ("referral_programs",
      f"{ID}, `brand` TEXT NOT NULL, `program` TEXT NOT NULL, `state` TEXT NOT NULL, `commission_model` TEXT NULL, `disclosure_required` INTEGER NOT NULL, {CT}",
      "UNIQUE(brand, program).", "UX(brand, program)"),
     ("referral_links",
      f"{ID}, `program_id` TEXT NOT NULL, `production_id` TEXT NULL, `code` TEXT NULL, `url` TEXT NULL, `state` TEXT NOT NULL, `validation_method` TEXT NOT NULL, `validation_evidence_ref` TEXT NULL, `validated_at` TEXT NOT NULL, `expires_at` TEXT NULL, `geo_json` TEXT NOT NULL, `platform_json` TEXT NOT NULL, `schema_version` TEXT NOT NULL, {CT}",
      "FK(program_id)->referral_programs. CHECK(state IN ('ACTIVE','EXPIRED','BLOCKED','REVIEW','UNVERIFIED','DISCOVERED')). "
      "CHECK(state<>'ACTIVE' OR (validation_method<>'HTTP_CHECK' AND validation_evidence_ref IS NOT NULL)).",
      "IX(program_id, state), IX(expires_at)"),
     ("attribution_events",
      f"{ID}, `publication_id` TEXT NOT NULL, `referral_link_id` TEXT NULL, `kind` TEXT NOT NULL, `value` REAL NULL, `provenance` TEXT NOT NULL, `occurred_at` TEXT NOT NULL, `ingested_at` TEXT NOT NULL",
      "FK(publication_id)->publications. CHECK(provenance IN ('API_MEASURED','IMPORTED','ESTIMATED')).",
      "IX(publication_id, occurred_at)"),
     ("revenue_events",
      f"{ID}, `publication_id` TEXT NULL, `referral_link_id` TEXT NULL, `amount` TEXT NOT NULL, `currency` TEXT NOT NULL, `state` TEXT NOT NULL, `provenance` TEXT NOT NULL, `external_ref` TEXT NULL, `occurred_at` TEXT NOT NULL, `confirmed_at` TEXT NULL",
      "CHECK(state IN ('PENDING','CONFIRMED','REVERSED','ADJUSTED')). CHECK(provenance IN ('API_MEASURED','IMPORTED','OPERATOR_ENTERED')). "
      "CHECK(provenance<>'ESTIMATED'). CHECK(state='REVERSED' OR amount NOT LIKE '-%') — a REVERSED row alone may be signed (V31-04).",
      "IX(publication_id), IX(state, occurred_at)"),
     ("analytics_snapshots",
      f"{ID}, `production_id` TEXT NOT NULL, `publication_id` TEXT NOT NULL, `metric` TEXT NOT NULL, `value` REAL NOT NULL, `unit` TEXT NULL, `currency` TEXT NULL, `provenance` TEXT NOT NULL, `window_start` TEXT NULL, `window_end` TEXT NULL, `schema_version` TEXT NOT NULL, `observed_at` TEXT NOT NULL, `source_account_id` TEXT NULL",
      "FK(publication_id)->publications. FK(source_account_id)->platform_accounts (migration 7; analytics.schema.json's own field, had no column until the fourth audit's contracts.fields_have_columns check found it). UNIQUE(publication_id, metric, window_start, provenance).",
      "IX(publication_id, metric), IX(observed_at)"),
    ]

    MODEL["Learning"] = [
     ("experiments",
      f"{ID}, `hypothesis` TEXT NOT NULL, `state` TEXT NOT NULL, `metric` TEXT NOT NULL, `min_sample` INTEGER NOT NULL, `started_at` TEXT NULL, `concluded_at` TEXT NULL, {CT}",
      "CHECK(state IN ('DRAFT','RUNNING','CONCLUDED','ABANDONED')).", "IX(state)"),
     ("experiment_variants",
      f"{ID}, `experiment_id` TEXT NOT NULL, `label` TEXT NOT NULL, `parameters_json` TEXT NOT NULL, `production_id` TEXT NULL, `result_json` TEXT NULL",
      "FK(experiment_id)->experiments. UNIQUE(experiment_id, label).", "UX(experiment_id, label)"),
     ("memory_records",
      f"{ID}, `scope` TEXT NOT NULL, `key` TEXT NOT NULL, `value_json` TEXT NOT NULL, `evidence_ref` TEXT NULL, `confidence` REAL NOT NULL, `schema_version` TEXT NOT NULL, {CT}",
      "UNIQUE(scope, key).", "UX(scope, key)"),
    ]

    MODEL["Control plane"] = [
     ("policies",
      f"{ID}, `key` TEXT NOT NULL, `current_version_id` TEXT NULL, `description` TEXT NOT NULL, {CT}",
      "UNIQUE(key).", "UX(key)"),
     ("policy_versions",
      f"{ID}, `policy_id` TEXT NOT NULL, `version_no` INTEGER NOT NULL, `body_sha256` TEXT NOT NULL, `body_ref` TEXT NOT NULL, `activated_at` TEXT NULL, `activated_by` TEXT NULL, `created_at` TEXT NOT NULL",
      "FK(policy_id)->policies. UNIQUE(policy_id, version_no). Immutable.", "UX(policy_id, version_no)"),
     ("policy_decisions",
      f"{ID}, `production_id` TEXT NULL, `action` TEXT NOT NULL, `decision` TEXT NOT NULL, `rule_key` TEXT NOT NULL, `policy_version_id` TEXT NOT NULL, `inputs_hash` TEXT NOT NULL, `correlation_id` TEXT NOT NULL, `decided_at` TEXT NOT NULL",
      "FK(policy_version_id)->policy_versions. CHECK(decision IN ('ALLOW','REQUIRE_APPROVAL','BLOCK')).",
      "IX(production_id), IX(decided_at), IX(decision)"),
     ("approvals",
      f"{ID}, `production_id` TEXT NULL, `action` TEXT NOT NULL, `scope_json` TEXT NOT NULL, `state` TEXT NOT NULL, `requested_at` TEXT NOT NULL, `decided_at` TEXT NULL, `decided_by` TEXT NULL, `expires_at` TEXT NOT NULL, `single_use` INTEGER NOT NULL DEFAULT 1, `consumed_at` TEXT NULL",
      "CHECK(state IN ('PENDING','APPROVED','REJECTED','EXPIRED','CONSUMED')). CHECK(single_use IN (0,1)).",
      "IX(state, expires_at), IX(production_id)"),
     ("model_registry",
      f"{ID}, `provider` TEXT NOT NULL, `model_id` TEXT NOT NULL, `capability` TEXT NOT NULL, `protocol` TEXT NOT NULL, `enabled` INTEGER NOT NULL DEFAULT 0, `constraints_json` TEXT NOT NULL, `pricing_snapshot_id` TEXT NULL, `last_verified_at` TEXT NULL, `fallback_order` INTEGER NOT NULL DEFAULT 100, {CT}",
      "UNIQUE(provider, model_id, capability). CHECK(enabled=0 OR last_verified_at IS NOT NULL).",
      "UX(provider, model_id, capability), IX(enabled, fallback_order)"),
     ("provider_health",
      f"{ID}, `provider` TEXT NOT NULL, `window_start` TEXT NOT NULL, `success_count` INTEGER NOT NULL, `failure_count` INTEGER NOT NULL, `timeout_count` INTEGER NOT NULL, `circuit_state` TEXT NOT NULL, `opened_at` TEXT NULL",
      "UNIQUE(provider, window_start). CHECK(circuit_state IN ('CLOSED','HALF_OPEN','OPEN')).", "UX(provider, window_start)"),
     ("notifications",
      f"{ID}, `severity` TEXT NOT NULL, `category` TEXT NOT NULL, `title` TEXT NOT NULL, `body` TEXT NOT NULL, `production_id` TEXT NULL, `acknowledged_at` TEXT NULL, `created_at` TEXT NOT NULL",
      "CHECK(severity IN ('INFO','WARNING','ERROR','CRITICAL')).", "IX(acknowledged_at), IX(created_at)"),
     ("backups",
      f"{ID}, `kind` TEXT NOT NULL, `path` TEXT NOT NULL, `sha256` TEXT NOT NULL, `bytes` INTEGER NOT NULL, `schema_version_at_backup` TEXT NOT NULL, `verified` INTEGER NOT NULL DEFAULT 0, `verified_at` TEXT NULL, `created_at` TEXT NOT NULL",
      "CHECK(kind IN ('PRE_MIGRATION','SCHEDULED','MANUAL','PRE_RESTORE')). CHECK(verified IN (0,1)).",
      "IX(created_at), IX(verified)"),
    ]

    tables = [t for fam in MODEL.values() for t in fam]
    names = [t[0] for t in tables]
    assert len(names) == len(set(names)), "duplicate table name"

    L = []
    L.append("# 11 — Database Schema Contract")
    L.append("")
    L.append("> **Generated artifact.** Emitted from `TOOLS/generate_artifacts.py`. `--check` diffs this file")
    L.append("> byte-for-byte against a fresh generation and fails the release gate on any difference (V31-01).")
    L.append("> `SPEC/10` defines the engine and transaction rules; this file defines every table.")
    L.append("")
    L.append("> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless documented exception; MAY = optional.")
    L.append("")
    L.append(f"**Tables: {len(names)}.** There is no table declared anywhere in this package without a column")
    L.append("contract below; the validator fails the build otherwise.")
    L.append("")
    L.append("## Conventions")
    L.append("")
    L.append("- All primary identifiers are **ULID** strings generated locally. External identifiers are never primary keys (D-003).")
    L.append("- All timestamps are **TEXT** in RFC 3339 UTC with explicit offset, validated as `date-time` by `FormatChecker` at every JSON Schema boundary, not merely declared (V31-02).")
    L.append("- Monetary values are **TEXT decimal strings** with six fractional digits (D-023). Most are **NonNegativeMoney**; only `cost_events.amount` (kind=ADJUSTMENT) and `revenue_events.amount` (state=REVERSED) may be signed (V31-04). `REAL` is never used for money.")
    L.append("- All booleans are `INTEGER` constrained by `CHECK(col IN (0,1))`.")
    L.append("- Every table carrying a persisted contract object has a `schema_version` column (D-004).")
    L.append("- `foreign_keys=ON` is asserted on every connection; a connection that reports it off aborts startup.")
    L.append("")
    L.append("## Table index")
    L.append("")
    L.append("| Family | Tables |")
    L.append("|---|---|")
    for fam, ts in MODEL.items():
        L.append(f"| {fam} | {', '.join('`'+t[0]+'`' for t in ts)} |")
    L.append("")
    for fam, ts in MODEL.items():
        L.append(f"## {fam}")
        L.append("")
        for name, cols, keys, idx in ts:
            L.append(f"### `{name}`")
            L.append("")
            L.append(f"**Columns.** {cols}")
            L.append("")
            L.append(f"**Keys and constraints.** {keys}")
            L.append("")
            L.append(f"**Indexes.** {idx}")
            L.append("")
    L.append("## Transaction boundaries")
    L.append("")
    L.append("These groupings are atomic. Each is one SQLite transaction; none of them contains a network call.")
    L.append("")
    L.append("| # | Atomic unit | Rows written together |")
    L.append("|---|---|---|")
    L.append("| TX-1 | Commit a production state change | `productions` (state + aggregate_version), `state_transitions`, `events`, `audit_log` when the action was policy-gated |")
    L.append("| TX-2 | Claim a job | `leases` (insert with fence token), `jobs` (state -> LEASED), `job_attempts` |")
    L.append("| TX-3 | Reserve budget | `budgets` (conditional update), `budget_reservations`, `cost_events` (kind=RESERVATION) |")
    L.append("| TX-4 | Settle cost | `budget_reservations` (-> SETTLED), `budgets` (release remainder), `cost_events` (kind=SETTLEMENT) |")
    L.append("| TX-5 | Create an external intent | `intents` (state=CREATED), `tool_runs`, `events`. **Committed before the network call, never inside it.** |")
    L.append("| TX-6 | Record an external outcome | `intents` (-> CONFIRMED/REFUTED/UNKNOWN), `publication_attempts`, `publications`, `events` |")
    L.append("| TX-7 | Seal a manifest | `artifact_manifests` (sealed=1), `artifact_versions` (-> CURRENT/SUPERSEDED), `productions.current_manifest_id`, `events` |")
    L.append("| TX-8 | Persist a QA report | `qa_reports`, `qa_findings`, `events` |")
    L.append("")
    L.append("## Concurrency rules")
    L.append("")
    L.append("1. Job claiming is a single conditional `UPDATE`. Read-then-write claiming is forbidden and is covered by a concurrency test (SPEC/73).")
    L.append("2. Budget reservation is a single conditional `UPDATE` with the limit in the `WHERE` clause. The check and the write are the same statement, so two workers cannot both pass the check.")
    L.append("3. Aggregate writes use `aggregate_version` optimistic concurrency. The `UNIQUE(aggregate_type, aggregate_id, aggregate_version)` index on `events` turns a lost update into a constraint violation rather than silent corruption.")
    L.append("4. `busy_timeout` is configured; long transactions are forbidden. No transaction may span a network call, a file render or a user prompt.")
    L.append("5. `synchronous=NORMAL` in steady state; `FULL` around migrations, backups and exports.")
    L.append("")
    return names, "\n".join(L)


# ===================================================================
# 3b. Canonical, EXECUTABLE SQLite DDL for the load-bearing tables (V3.1.1)
#
# build_tables_and_doc() above emits SPEC/11 as prose documentation -- readable,
# but its "keys" column mixes real SQL fragments with narrative commentary and is
# not meant to be executed. Before V3.1.1 (the defect this closes), every test
# that needed to prove a database CHECK constraint actually rejects bad data had
# hand-copied its own guess at that constraint as a literal DDL string inside the
# test file (TOOLS/test_synthetic_disclosure.py, TOOLS/test_platform_evidence.py).
# That means the test could pass even if the REAL contract in this generator had
# drifted or been broken, because it was never asked to enforce anything -- the
# test enforced its own copy.
#
# build_canonical_ddl() is the fix: real, executable `CREATE TABLE` statements,
# built from the exact same module-level vocabularies (ALL_STATES,
# PUBLICATION_STATES, AUTHORITATIVE_EVIDENCE, PLATFORM_CAPABILITY_*) that the
# JSON Schemas above are built from, so a test that loads this can never observe
# a different contract than the one the schemas encode.
#
# Scope: this function covers the tables whose CHECK constraints are exercised by
# the test suite (the load-bearing subset for structural conformance testing),
# not the package's full ~40-table catalogue -- SPEC/11 remains the prose
# reference for the rest. Extend this dict, not a test file, when a new table's
# constraint needs an executable test.
# ===================================================================

def build_canonical_ddl():
    """Returns collections.OrderedDict {table_name: CREATE TABLE SQL (executable SQLite)}."""
    def sql_list(values):
        return ", ".join(repr(v) for v in values)

    D = collections.OrderedDict()

    D["synthetic_declarations"] = (
        "CREATE TABLE synthetic_declarations (\n"
        "  id TEXT PRIMARY KEY\n"
        "  -- abbreviated: this test fixture only needs the FK target to exist;\n"
        "  -- the full column set is documented in SPEC/11 under synthetic_declarations.\n"
        ");"
    )

    D["publications"] = (
        # SQL requires every column definition before any table-level constraint
        # (CHECK / FOREIGN KEY not attached to a single column); mixing the two
        # orders is a syntax error in SQLite, so all columns come first here.
        "CREATE TABLE publications (\n"
        "  id TEXT PRIMARY KEY,\n"
        "  state TEXT NOT NULL,\n"
        "  external_id TEXT NULL,\n"
        "  evidence_source TEXT NULL,\n"
        "  evidence_retrieved_at TEXT NULL,\n"
        "  synthetic_declaration_id TEXT NULL,\n"
        "  platform_label_required INTEGER NOT NULL DEFAULT 0,\n"
        "  synthetic_label_applied INTEGER NOT NULL DEFAULT 0,\n"
        "  FOREIGN KEY (synthetic_declaration_id) REFERENCES synthetic_declarations(id),\n"
        f"  CHECK (state IN ({sql_list(PUBLICATION_STATES)})),\n"
        "  CHECK (platform_label_required = 0 OR synthetic_declaration_id IS NOT NULL),\n"
        "  CHECK (state <> 'VERIFIED' OR (external_id IS NOT NULL\n"
        f"         AND evidence_source IN ({sql_list(AUTHORITATIVE_EVIDENCE)})\n"
        "         AND evidence_retrieved_at IS NOT NULL)),\n"
        "  CHECK (state <> 'VERIFIED' OR platform_label_required = 0 OR synthetic_label_applied = 1)\n"
        ");"
    )

    D["platform_capabilities"] = (
        "CREATE TABLE platform_capabilities (\n"
        "  platform TEXT NOT NULL,\n"
        "  account_id TEXT NOT NULL,\n"
        "  capability TEXT NOT NULL,\n"
        "  status TEXT NOT NULL,\n"
        "  evidence_source TEXT NOT NULL,\n"
        "  verified_at TEXT NOT NULL,\n"
        "  expires_at TEXT NULL,\n"
        "  PRIMARY KEY (platform, account_id, capability),\n"
        f"  CHECK (status IN ({sql_list(PLATFORM_CAPABILITY_STATUS)})),\n"
        "  CHECK (status <> 'VERIFIED' OR evidence_source IN\n"
        f"    ({sql_list(PLATFORM_CAPABILITY_AUTHORITATIVE_EVIDENCE)}))\n"
        ");"
    )

    return D


def build_ddl_sql_text():
    """Renders build_canonical_ddl() as the standalone SCHEMAS/schema.sql artifact."""
    L = [
        "-- AMCCA canonical SQLite DDL (generated artifact).",
        f"-- Emitted from `TOOLS/generate_artifacts.py` (schema_version {SV}).",
        "-- `--check` diffs this file byte-for-byte against a fresh generation (V31-01).",
        "-- Do not edit by hand; edit build_canonical_ddl() in generate_artifacts.py and run --regen.",
        "--",
        "-- Scope: the load-bearing subset of tables whose CHECK constraints are exercised",
        "-- by TOOLS/test_*.py (V31.1.1 D-DUP-01). This is NOT the full ~40-table catalogue;",
        "-- SPEC/11_DATABASE_SCHEMA.md remains the prose reference for every table.",
        "",
    ]
    for name, sql in build_canonical_ddl().items():
        L.append(f"-- {name}")
        L.append(sql)
        L.append("")
    return "\n".join(L)


# ===================================================================
# 4. Traceability (canonical)
# ===================================================================

def build_traceability(root):
    spec = sorted(f for f in os.listdir(os.path.join(root, "SPEC")) if f.endswith(".md"))
    bp = sorted(f for f in os.listdir(os.path.join(root, "BLUEPRINT"))
                if f.endswith(".md") and not f.startswith("11_"))

    GROUPS = [
     ("01-09","Foundations","Stack, runtime, configuration, contracts, errors, agents, tools, policy, approvals"),
     ("10-19","Persistence and durable execution","Database, state machine, jobs, idempotency, recovery, scheduling, artifacts, storage"),
     ("20-29","Cost, intelligence and evidence","Budgets, pricing, memory, gateway, routing, health, research"),
     ("30-39","Content production","Strategy, hooks, script, media, QA, rights, rework, prompts, localisation"),
     ("40-49","Distribution and money","Platforms, OAuth, publishing, synthetic disclosure, referrals, analytics, preflight"),
     ("50-59","Security, privacy and operations","Security, privacy, retention, kill switch, observability, events, backup, export, versioning, dependencies"),
     ("60-69","Interface and internal boundaries","UI, flows, state, notifications, internal API, optional HTTP boundary, performance, concurrency, time, diagnostics"),
     ("70-79","Verification and release","Testing, matrices, security/concurrency/chaos/acceptance suites, packaging, installation, release, definition of done"),
     ("80-89","Implementation","Plan, agent contracts, tool contracts, execution notes"),
    ]
    BPMAP = {
     "00_MASTER_BLUEPRINT.md": "README.md, DECISIONS.md, SYSTEM.md, ARCHITECTURE.md",
     "01_SYSTEM_CONTEXT.md": "SPEC/23, SPEC/27, SPEC/41, SPEC/50",
     "02_COMPONENT_MAP.md": "SPEC/06, SPEC/07, SPEC/08, SPEC/14, SPEC/17, SPEC/33",
     "03_END_TO_END_RUNTIME.md": "SPEC/12, SPEC/13, SPEC/26, SPEC/32, SPEC/35, SPEC/44",
     "04_STATE_AND_DATAFLOW.md": "SPEC/10, SPEC/11, SPEC/18, SPEC/19, SPEC/47, SPEC/55",
     "05_AUTONOMY_POLICY_APPROVALS.md": "SPEC/08, SPEC/09, SPEC/53, POLICIES/AUTONOMY_POLICY.md",
     "06_EXTERNAL_INTEGRATIONS.md": "SPEC/15, SPEC/23, SPEC/41, SPEC/42, SPEC/43",
     "07_FAILURE_RECOVERY_COST_STORAGE.md": "SPEC/05, SPEC/16, SPEC/20, SPEC/21, SPEC/52",
     "08_SECURITY_OBSERVABILITY_TESTING.md": "SPEC/28, SPEC/50, SPEC/54, SPEC/70, SPEC/72",
     "09_DEPLOYMENT_AND_UI.md": "SPEC/60, SPEC/61, SPEC/76, SPEC/77",
     "10_OPERATIONAL_INVARIANTS.md": "SPEC/71 (test matrix), TOOLS/validate_package.py, TOOLS/generate_artifacts.py",
    }
    L = ["# 11 — Traceability Map", "",
     "> **Generated artifact.** Emitted from the real file listing by `TOOLS/generate_artifacts.py`.",
     "> `--check` fails the build if any SPEC file is absent from this map, so it cannot silently fall",
     "> out of date.", "",
     f"**SPEC documents: {len(spec)}.** Numbering is contiguous 01-83 with no duplicates and no two documents",
     "covering the same subject (D-022).", "",
     "## Blueprint to SPEC", "", "| Blueprint document | Detailed by |", "|---|---|"]
    for f in bp:
        L.append(f"| `BLUEPRINT/{f}` | {BPMAP.get(f, '—')} |")
    L += ["", "## SPEC index by band", ""]
    for band, title, desc in GROUPS:
        lo, hi = band.split("-")
        members = [f for f in spec if lo <= f[:2] <= hi]
        if not members: continue
        L.append(f"### {band} — {title}")
        L.append("")
        L.append(f"{desc}.")
        L.append("")
        L.append("| Document | Subject |")
        L.append("|---|---|")
        for f in members:
            subject = f[3:-3].replace("_", " ").title()
            L.append(f"| `SPEC/{f}` | {subject} |")
        L.append("")
    L += ["## Generated artifacts", "",
     "| Artifact | Generator | Rule |", "|---|---|---|",
     "| `SPEC/11_DATABASE_SCHEMA.md` | `generate_artifacts.build_tables_and_doc` | V31-01: `--check` diffs byte-for-byte |",
     "| `SPEC/13_STATE_TRANSITION_MATRIX.md` | `generate_artifacts.build_state_matrix_md` | same |",
     "| `SCHEMAS/*.schema.json` | `generate_artifacts.build_schemas` | same |",
     "| `SCHEMAS/state-machine.json` | `generate_artifacts.build_state_machine_json` | same |",
     "| `SCHEMAS/tables.json` | `generate_artifacts.build_tables_and_doc` | same |",
     "| `SCHEMAS/schema.sql` | `generate_artifacts.build_canonical_ddl` | same (V31.1.1: executable DDL for load-bearing tables) |",
     "| `BLUEPRINT/11_TRACEABILITY.md` | `generate_artifacts.build_traceability` | same |",
     "| `MANIFEST.md`, `MANIFEST.sha256` | `TOOLS/validate_package.py --regen` | excludes itself |", ""]
    return "\n".join(L)


# ===================================================================
# Orchestration: build the full set of generated artifacts
# ===================================================================

def generate_all(root):
    """Returns {relpath: content_str} for every canonical-generated artifact."""
    T = build_transitions()
    errs = validate_state_machine(T)
    if errs:
        raise SystemExit("STATE MACHINE INVALID:\n" + "\n".join("  - " + e for e in errs))

    sm_json = build_state_machine_json(T)
    prod_states = sm_json["states"]
    prod_state_names = [s["name"] for s in prod_states]

    out = {}
    out["SCHEMAS/state-machine.json"] = json.dumps(sm_json, indent=2, ensure_ascii=False) + "\n"
    out["SPEC/13_STATE_TRANSITION_MATRIX.md"] = build_state_matrix_md(T)

    schemas = build_schemas(prod_state_names)
    for name, doc in schemas.items():
        out[f"SCHEMAS/{name}.schema.json"] = json.dumps(doc, indent=2, ensure_ascii=False) + "\n"

    table_names, db_doc = build_tables_and_doc()
    out["SPEC/11_DATABASE_SCHEMA.md"] = db_doc
    out["SCHEMAS/tables.json"] = json.dumps({"schema_version": SV, "tables": table_names}, indent=2) + "\n"
    out["SCHEMAS/schema.sql"] = build_ddl_sql_text() + "\n"

    out["BLUEPRINT/11_TRACEABILITY.md"] = build_traceability(root)
    return out


def write_all(root, artifacts):
    for rel, content in artifacts.items():
        path = os.path.join(root, *rel.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(content)


def check_all(root):
    """Generate fresh, diff byte-for-byte against what's on disk. Returns (ok, diffs)."""
    fresh = generate_all(root)
    diffs = []
    for rel, content in fresh.items():
        path = os.path.join(root, *rel.split("/"))
        if not os.path.exists(path):
            diffs.append(f"{rel}: MISSING on disk")
            continue
        with open(path, "r", encoding="utf-8") as f:
            current = f.read()
        if current != content:
            diffs.append(f"{rel}: DRIFT DETECTED (generated content differs from checked-in file)")
    return (not diffs), diffs


def main():
    ap = argparse.ArgumentParser()
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument("--regen", action="store_true", help="write generated artifacts to disk")
    g.add_argument("--check", action="store_true", help="diff generated artifacts against disk; fail on drift")
    args = ap.parse_args()

    if args.regen:
        artifacts = generate_all(ROOT)
        write_all(ROOT, artifacts)
        print(f"regenerated {len(artifacts)} artifacts")
        return 0

    ok, diffs = check_all(ROOT)
    if ok:
        print("PASS  no generated-artifact drift detected")
        return 0
    print("FAIL  generated artifact drift detected")
    for d in diffs:
        print("  -", d)
    return 1


if __name__ == "__main__":
    sys.exit(main())
