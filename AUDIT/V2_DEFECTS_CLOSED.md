# V2 Defects — Closure Trace

> **This document does not certify anything** (D-029). It maps each defect found in the independent
> audit of AMCCA Engineering Specification V2 to the concrete change in V3, and names the executable
> check that demonstrates the closure. Where no executable check exists, that is stated plainly.
>
> A second, independent audit of V3.0.0 itself found ten further defects — mostly gaps between what V3
> *documented* as a guarantee and what its own tooling actually *checked*. Those are closed in V3.1; see
> `CHANGELOG_V3_TO_V3.1.md`. The pattern repeats for a reason worth naming: a specification package is
> exactly as trustworthy as its weakest unchecked claim, and finding the next one is expected, not
> alarming, as long as each one gets an executable check rather than a stronger sentence.
>
> The V2 package failed precisely here: `AUDIT/FINAL_AUDIT.md` declared eighteen dimensions `RESOLVED`
> and the package `implementation-ready`, when four of those dimensions contained its worst defects.
> A document cannot audit itself. Run the checks.

```
python TOOLS/validate_package.py
python TOOLS/conformance_tests.py
```

---

## Contract defects

| # | V2 defect | V3 correction | Demonstrated by |
|---|---|---|---|
| 1 | `production.schema.json` declared `"status": {"type": "string"}` with no enum, while `job` and `publication` both had enums | `production.state` enum is generated from `SCHEMAS/state-machine.json` and cannot diverge | `stm.production_enum_matches`; conformance `production/state outside the state machine` |
| 2 | Vocabulary split: `status` on production, `state` on job and publication | Single vocabulary: `state` everywhere | Schema inspection; conformance suite |
| 3 | Six of nine schemas lacked `schema_version`, violating D-004 | All fifteen schemas carry `schema_version` as a `const` | `schemas.every_schema_versioned`; conformance `production/missing schema_version` |
| 4 | No schema linked any record to a production; only `manifest` had `production_id` | Every non-root aggregate carries `production_id` | Schema inspection |
| 5 | All schemas set `additionalProperties: false`, which **forbade** adding the production link required by point 4 | Links are declared properties, so the constraint and the requirement no longer contradict | `schemas.valid_draft_2020_12` |
| 6 | `event.schema.json` contradicted D-018: the decision required `correlation_id` and `causation_id`; the schema neither declared nor permitted them | D-018 amended and the schema now requires `correlation_id`, permits `causation_id`, and adds `transition_id` | Conformance `event/event with correlation and causation`, `event/event without correlation_id` |
| 7 | No `config.schema.json` existed, so configuration was unvalidated | `config.schema.json` added; startup aborts on failure | `config.example_validates_against_schema` |
| 8 | No contract could express "this QA result belongs to this production and this artifact version" | `qa.schema.json` requires both, plus `responsible_artifact_version_id` per finding | Conformance `qa/finding without a responsible artifact` |

## State machine defects

| # | V2 defect | V3 correction | Demonstrated by |
|---|---|---|---|
| 9 | 28 states declared, 24 present in the transition matrix | 32 states, 198 transitions, generated from one source | `stm.matrix_lists_all_transitions` |
| 10 | `REWORK`, `ARCHIVED` and `FAILED` were unreachable — no transition entered them | Every state except `INIT` has an inbound transition | `stm.every_state_has_inbound` |
| 11 | `REWORK`, `BLOCKED`, `FAILED`, `ARCHIVED`, `PUBLICATION_VERIFIED` and `UNKNOWN_EXTERNAL_STATE` had **no outbound transitions**. The safety state the whole architecture depends on had no way out | Every non-terminal state has an outbound transition; terminals have none | `stm.every_non_terminal_has_outbound`, `stm.terminals_have_no_outbound` |
| 12 | No QA-failure branches existed at all | Fourteen states route to `REWORK` on defect detection | `SPEC/13` matrix |
| 13 | No state represented asset or audio generation, so rework had no legal state to return to | `STORYBOARDING`, `ASSET_GENERATION` and `AUDIO_GENERATION` added as producing states | `SPEC/12` state families |
| 14 | Resume from `BLOCKED` and exit from `UNKNOWN_EXTERNAL_STATE` were undecidable — nothing recorded where to return to | `blocked_from`, `unknown_from` and `rework_attempts` persisted on `productions` with `CHECK` constraints | `SPEC/11` `productions` contract |
| 15 | Production-level publish states conflated with per-target publication state | Rollup rules R-1 to R-5; a partial outcome is never promoted | `SPEC/13` rollup section |
| 16 | No `CANCELLED` state for productions despite jobs having one | `CANCELLED` added as a terminal state | State inventory |

## Database defects

| # | V2 defect | V3 correction | Demonstrated by |
|---|---|---|---|
| 17 | 44 tables declared in `10_DATABASE.md`, only 32 given columns in `10_DATABASE_SCHEMA.md` | 58 tables, every one with columns, keys, constraints and indexes | `db.every_table_has_contract` |
| 18 | `leases`, `publications`, `schema_migrations`, `agent_runs`, `tool_runs`, `policies` and six others had no column contract | All present | Same check |
| 19 | No table could be referenced without a contract because nothing checked | Every table name appearing anywhere must have a contract | `db.no_table_referenced_without_contract` |
| 20 | Transaction boundaries were described in prose without enumeration | Eight enumerated atomic units TX-1 to TX-8 | `db.contains[TX-1]`, `db.contains[TX-8]` |
| 21 | Money had no defined storage type | Decimal string, six fractional digits, `CHECK`-constrained (D-023) | Conformance `job/money as a float`, `cost-event/float amount` |

## Package structure defects

| # | V2 defect | V3 correction | Demonstrated by |
|---|---|---|---|
| 22 | Two divergent Blueprints shipped in two archives, and the **hashed** archive contained the superseded five-document version | One package, one Blueprint (D-022) | Package listing |
| 23 | `ANTIGRAVITY_START_PROMPT.md` never mentioned Blueprint V2.1, so an implementation agent would have used the obsolete one | Single start prompt naming a single package; explicitly instructs discarding V2 archives | `ANTIGRAVITY_START_PROMPT.md` |
| 24 | Two files numbered `10_` | Contiguous unique numbering 01-83 | `spec.unique_numbering` |
| 25 | Two Definitions of Done with different criteria, which means none | One, at `SPEC/79` | `spec.single[DEFINITION_OF_DONE]` |
| 26 | Two testing strategy documents with different scope | One, at `SPEC/70` | `spec.single[TESTING_STRATEGY]` |
| 27 | Error taxonomy declared 16 categories; the catalogue covered 9 codes | One file, 17 categories, 40 catalogued codes, retry disposition per category | `refs.all_error_codes_catalogued` |
| 28 | API document count disagreed with itself (46 vs 47) and referenced an OpenAPI file by a path that did not resolve | `SPEC/65` plus `SCHEMAS/openapi.yaml`, both present and referenced correctly | `refs.all_internal_references_resolve` |
| 29 | The Blueprint had no rank in the source-of-truth order despite containing fifteen normative invariants | D-021 ranks it; `BLUEPRINT/10` is normative and overrides conflicting SPEC text | `DECISIONS.md` |
| 30 | Median SPEC file was 89 words; the package was a reference architecture presented as a specification | Every SPEC file carries substantive normative content and a normative-language header | `spec.normative_header_present` |
| 31 | `MANIFEST.md` contained its own hash, which is logically impossible — 120/121 hashes verified by construction | Manifest excludes itself and `MANIFEST.sha256` | `manifest.excludes_itself`, `manifest.matches_tree` |

## Configuration defects

| # | V2 defect | V3 correction | Demonstrated by |
|---|---|---|---|
| 32 | Two budget vocabularies across two files (`production_eur` vs `per_production_eur`) with no schema to catch it | One vocabulary defined once in `config.schema.json` | `config.single_budget_vocabulary` |
| 33 | Daily cap 20 against monthly cap 300 with no precedence rule and no consistency check | Precedence defined in `SPEC/20`; preflight rejects inconsistency with `AMCCA-CFG-004` | `config.budget_consistency_rule` |
| 34 | `environments.yaml` treated `dry_run` as a boolean while `README` listed it as a mode | Environment and flags are explicitly two orthogonal axes | `CONFIG/environments.yaml`, `README.md` |

## Coverage gaps

| # | V2 defect | V3 correction | Demonstrated by |
|---|---|---|---|
| 35 | "AI-generated", "synthetic", "watermark", "C2PA", "AI Act" appeared **zero times** across 133 files, in a system for publishing AI-generated video | `SPEC/45`, `POLICIES/SYNTHETIC_CONTENT_POLICY.md`, `synthetic_declarations` table, invariant I-18, preflight gate, autonomy matrix row blocked in every mode | `gaps.covers[synthetic]`, `gaps.covers[C2PA]`, `gaps.covers[AI Act]` |
| 36 | No coverage of personal data or GDPR anywhere; "disclosure" meant only commercial affiliation | `SPEC/51`, `POLICIES/PRIVACY_POLICY.md`, `claims.contains_personal_data`, retention class, export exclusion (D-027) | `gaps.covers[personal data]`, `gaps.covers[GDPR]` |
| 37 | No distinction between estimated and measured values; forecasts could enter revenue | Separate types, separate tables, `CHECK` forbidding `ESTIMATED` provenance in `revenue_events` (D-030, I-13); analytics unique key includes provenance (I-12) | Conformance `analytics/observation without provenance` |
| 38 | QA verdicts could in principle be set from AI assessment | Deterministic verdict; `check_kind` discriminator; `AMCCA-QA-002` (D-024, I-19) | `SPEC/35`, `qa.schema.json` |

## Fact-checking defects

| # | V2 defect | V3 correction | Demonstrated by |
|---|---|---|---|
| 39 | `SPEC/20_OMNIROUTERS.md` asserted a base URL, auth scheme, routes and an `X-Oneapi-Request-Id` header with no source and no timestamp, violating V2's own `FACT_CHECKING_POLICY` | `SPEC/23` marks all gateway facts `UNVERIFIED`; `CONFIG/providers.yaml` carries a null `evidence` block and `capabilities_verified: false` | `config.gateway_unverified_by_default` |
| 40 | Service identity ambiguous — at least four projects share near-identical names | Stated explicitly in `SPEC/23` and `CONFIG/providers.yaml` with an identity-confirmation obligation | Same |
| 41 | No mechanism made external facts expire | `source_ref` and `retrieved_at` required on platform rules and media profiles; a stale rule set degrades the capability to `UNVERIFIED` (D-028) | `config.platform_rules_carry_evidence` |
| 42 | The final audit's own word count was not reproducible (11,962 claimed; 12,112 with the manifest, 11,253 without) | No self-certifying audit exists; this file defers to executable checks (D-029) | This paragraph |

---

## What remains unverified in V3

Closing V2's defects is not the same as certifying V3. These are the things this package **cannot**
demonstrate, stated so that nobody mistakes a green validator for a guarantee:

1. **The gateway.** Identity, endpoints, auth and capabilities of the intended AI provider are unverified.
   `SPEC/23` and `CONFIG/providers.yaml` say so and refuse autonomous operation until probed.
2. **Platform rules.** The disclosure requirements in `CONFIG/platforms.yaml` come from secondary sources
   retrieved on 2026-09-02 and are marked `UNVERIFIED_SECONDARY_SOURCE`. The primary sources are each
   platform's own help pages.
3. **The transitional scope of AI Act Article 50(2).** The 2026-08-02 application date is well sourced;
   the reported 2026-12-02 machine-readable-marking transition is less certain and is flagged as such in
   `SPEC/45` and `CONFIG/platforms.yaml`.
4. **Legal applicability.** Nothing here is legal advice. Whether a given production triggers a given duty
   is a question for a qualified adviser.
5. **Budget realism.** `per_production: 5.00 EUR` has not been validated against real provider pricing.
   `CONFIG/budgets.yaml` says so.
6. **Correctness against reality.** The validator proves this package is internally consistent. It cannot
   prove the specification is *right* — that the architecture will work, that the invariants are the
   correct ones, or that an implementation of it will behave as described. Only building it will show that.

Point 6 is the honest ceiling of any specification package, and V2's central failure was writing a
document that claimed to have exceeded it.
