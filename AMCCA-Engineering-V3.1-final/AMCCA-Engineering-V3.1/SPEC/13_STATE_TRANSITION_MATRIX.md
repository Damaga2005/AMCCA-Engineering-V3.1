# 13 — Production State Transition Matrix

> **Generated artifact.** This file is emitted from `TOOLS/generate_artifacts.py`.
> `TOOLS/generate_artifacts.py --check` compares the current file byte-for-byte against a
> fresh generation and fails the release gate on any difference (V31-01). Do not edit it by
> hand; edit the canonical model in `generate_artifacts.py` and run `--regen`.

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless documented exception; MAY = optional.

**States:** 32 — **Transitions:** 198 — **Terminal states:** ARCHIVED, FAILED, CANCELLED

## Structural guarantees

The following properties are machine-verified on every build and are release gates (`SPEC/79`, criterion 4):

1. Every state except `INIT` has at least one inbound transition.
2. Every non-terminal state has at least one outbound transition.
3. No terminal state has an outbound transition.
4. Every state is reachable from `INIT` by forward graph traversal.
5. Every state can reach at least one terminal state.
6. Transition IDs are unique; there are no self-loops.
7. Any transition not listed in this matrix is illegal and MUST fail closed with `AMCCA-STM-001`.

## State inventory

| State | Kind | In | Out | Meaning |
|---|---|--:|--:|---|
| `INIT` | initial | 1 | 4 | Production record created; nothing generated yet. |
| `RESEARCHING` | producing | 4 | 6 | Research engine gathering timestamped evidence. |
| `RESEARCH_VERIFIED` | verified | 2 | 4 | Claims linked to sources; evidence thresholds met. |
| `CONCEPT_SELECTED` | gate | 2 | 4 | Strategy decision recorded; concept locked. |
| `SCRIPTING` | producing | 4 | 6 | Script agent generating script versions. |
| `SCRIPT_VERIFIED` | verified | 2 | 4 | Script passes schema, factual and policy validation. |
| `STORYBOARDING` | producing | 4 | 6 | Storyboard agent generating scene plan. |
| `STORYBOARD_VERIFIED` | verified | 2 | 4 | Storyboard structurally valid and script-aligned. |
| `ASSET_GENERATION` | producing | 4 | 6 | Visual assets generated or sourced. |
| `ASSETS_READY` | verified | 2 | 4 | All required assets present with GREEN rights. |
| `AUDIO_GENERATION` | producing | 4 | 6 | Voice and music tracks generated. |
| `AUDIO_READY` | verified | 2 | 4 | Audio passes deterministic technical checks. |
| `EDITING` | producing | 4 | 5 | MediaWorker composing the candidate render. |
| `CANDIDATE_RENDERED` | verified | 2 | 5 | Render artifact exists, hashed and manifest-consistent. |
| `TECHNICAL_QA` | qa | 3 | 6 | Container, codec, duration, decode integrity. |
| `VISUAL_QA` | qa | 3 | 6 | Black/freeze frames, safe areas, visual coherence. |
| `AUDIO_QA` | qa | 3 | 6 | Silence, clipping, loudness, A/V sync, intelligibility. |
| `CONTENT_QA` | qa | 3 | 6 | Factual accuracy, tone, policy and claim substantiation. |
| `RETENTION_QA` | qa | 3 | 6 | Hook strength and retention heuristics. |
| `COMPLIANCE_QA` | qa | 3 | 6 | Rights, disclosure, synthetic labelling, platform policy. |
| `SCORING` | gate | 2 | 5 | Aggregate scoring against configured thresholds. |
| `REWORK` | control | 15 | 10 | Targeted regeneration of the earliest invalid DAG node. |
| `FINAL_VERIFIED` | verified | 2 | 5 | All gates passed; production manifest sealed. |
| `READY_TO_PUBLISH` | gate | 2 | 5 | Publication preflight passed for at least one target. |
| `PUBLISHING` | publish | 3 | 6 | Publication intents dispatched to platform adapters. |
| `PUBLICATION_PROCESSING` | publish | 4 | 7 | At least one target accepted and is processing. |
| `PUBLICATION_VERIFIED` | publish | 4 | 5 | All required targets verified by authoritative evidence. |
| `ARCHIVED` | terminal | 2 | 0 | Lifecycle closed; artifacts under retention policy. |
| `BLOCKED` | control | 31 | 30 | Halted by policy, budget, rights, credential or kill switch. |
| `FAILED` | terminal | 31 | 0 | Permanently failed; not resumable without a new production. |
| `CANCELLED` | terminal | 29 | 0 | Cancelled by an authorised operator. |
| `UNKNOWN_EXTERNAL_STATE` | control | 16 | 21 | An external side effect may or may not have taken place. |

## Transition matrix

`Actor` is the only component permitted to commit the transition. Agents never appear as an actor:
they submit results, the Orchestrator commits state (D-015, invariant I-09).

| ID | From | To | Trigger | Required evidence / guard | Actor |
|---|---|---|---|---|---|
| `T-001` | `INIT` | `RESEARCHING` | `start_production` | Preflight PASS; opportunity_id resolvable; budget reservation for research committed | Orchestrator |
| `T-002` | `RESEARCHING` | `RESEARCH_VERIFIED` | `research_completed` | ≥ policy.min_sources independent sources; every material claim has source_id + retrieved_at; no RED source | Orchestrator |
| `T-003` | `RESEARCH_VERIFIED` | `CONCEPT_SELECTED` | `concept_chosen` | Strategy decision persisted with rationale and expected-value snapshot | Orchestrator |
| `T-004` | `CONCEPT_SELECTED` | `SCRIPTING` | `begin_scripting` | Budget reservation for scripting committed; prompt_version pinned | Orchestrator |
| `T-005` | `SCRIPTING` | `SCRIPT_VERIFIED` | `script_validated` | Script matches script schema; every factual line maps to a verified claim; CONTENT_POLICY PASS | Orchestrator |
| `T-006` | `SCRIPT_VERIFIED` | `STORYBOARDING` | `begin_storyboard` | Budget reservation for storyboard committed | Orchestrator |
| `T-007` | `STORYBOARDING` | `STORYBOARD_VERIFIED` | `storyboard_validated` | Scene count > 0; every scene references a script segment; durations sum within tolerance | Orchestrator |
| `T-008` | `STORYBOARD_VERIFIED` | `ASSET_GENERATION` | `begin_assets` | Budget reservation for assets committed; media profile resolved | Orchestrator |
| `T-009` | `ASSET_GENERATION` | `ASSETS_READY` | `assets_validated` | Every storyboard scene has ≥1 asset; every asset has rights_status = GREEN; duplicate check PASS | Orchestrator |
| `T-010` | `ASSETS_READY` | `AUDIO_GENERATION` | `begin_audio` | Budget reservation for audio committed | Orchestrator |
| `T-011` | `AUDIO_GENERATION` | `AUDIO_READY` | `audio_validated` | Voice track decodes; loudness within profile; no clipping; duration aligns to script | Orchestrator |
| `T-012` | `AUDIO_READY` | `EDITING` | `begin_edit` | All upstream artifacts present and hash-verified; disk headroom ≥ config.storage.minimum_free_gb | Orchestrator |
| `T-013` | `EDITING` | `CANDIDATE_RENDERED` | `render_completed` | FFmpeg exit 0; output file hashed; artifact manifest consistent with DAG | Orchestrator |
| `T-014` | `CANDIDATE_RENDERED` | `TECHNICAL_QA` | `begin_qa` | Render artifact readable and probe-able | Orchestrator |
| `T-015` | `TECHNICAL_QA` | `VISUAL_QA` | `technical_qa_pass` | All deterministic technical checks PASS or WARN | Orchestrator |
| `T-016` | `VISUAL_QA` | `AUDIO_QA` | `visual_qa_pass` | Visual checks ≥ threshold; no CRITICAL finding | Orchestrator |
| `T-017` | `AUDIO_QA` | `CONTENT_QA` | `audio_qa_pass` | Audio checks ≥ threshold; no CRITICAL finding | Orchestrator |
| `T-018` | `CONTENT_QA` | `RETENTION_QA` | `content_qa_pass` | Factual accuracy ≥ 8.0; no unsubstantiated material claim | Orchestrator |
| `T-019` | `RETENTION_QA` | `COMPLIANCE_QA` | `retention_qa_pass` | Retention heuristics recorded; no CRITICAL finding | Orchestrator |
| `T-020` | `COMPLIANCE_QA` | `SCORING` | `compliance_qa_pass` | Rights GREEN; required affiliate disclosure present; synthetic-content label present per SPEC/45; platform policy PASS | Orchestrator |
| `T-021` | `SCORING` | `FINAL_VERIFIED` | `score_accepted` | overall_score ≥ policy.qa.overall_min AND every critical dimension ≥ policy.qa.critical_min | Orchestrator |
| `T-022` | `FINAL_VERIFIED` | `READY_TO_PUBLISH` | `publication_preflight_pass` | ≥1 target with capability VERIFIED, credential valid, metadata version sealed, referral version valid | Orchestrator |
| `T-023` | `READY_TO_PUBLISH` | `PUBLISHING` | `dispatch_publication` | Publication lock acquired; publication intents persisted; kill switch not engaged; publishing_enabled = true | Orchestrator |
| `T-024` | `PUBLISHING` | `PUBLICATION_PROCESSING` | `targets_accepted` | ≥1 publication in {UPLOADED, PROCESSING} and none in UNKNOWN_EXTERNAL_STATE (rollup rule R-2) | Orchestrator |
| `T-025` | `PUBLICATION_PROCESSING` | `PUBLICATION_VERIFIED` | `all_targets_verified` | Every required publication is VERIFIED by authoritative platform evidence (rollup rule R-1); a resolving-URL check alone is never sufficient (V31-06) | Orchestrator |
| `T-026` | `PUBLICATION_VERIFIED` | `ARCHIVED` | `archive` | Analytics baseline captured; retention policy applied | Orchestrator |
| `T-027` | `FINAL_VERIFIED` | `ARCHIVED` | `shelve` | Operator archives a verified production without publishing | Operator |
| `T-101` | `SCRIPTING` | `REWORK` | `defect_detected` | ≥1 finding with severity ≥ policy.rework.min_severity; responsible artifact resolvable in the DAG; rework attempts < policy.rework.max_attempts | Orchestrator |
| `T-102` | `STORYBOARDING` | `REWORK` | `defect_detected` | ≥1 finding with severity ≥ policy.rework.min_severity; responsible artifact resolvable in the DAG; rework attempts < policy.rework.max_attempts | Orchestrator |
| `T-103` | `ASSET_GENERATION` | `REWORK` | `defect_detected` | ≥1 finding with severity ≥ policy.rework.min_severity; responsible artifact resolvable in the DAG; rework attempts < policy.rework.max_attempts | Orchestrator |
| `T-104` | `AUDIO_GENERATION` | `REWORK` | `defect_detected` | ≥1 finding with severity ≥ policy.rework.min_severity; responsible artifact resolvable in the DAG; rework attempts < policy.rework.max_attempts | Orchestrator |
| `T-105` | `CANDIDATE_RENDERED` | `REWORK` | `defect_detected` | ≥1 finding with severity ≥ policy.rework.min_severity; responsible artifact resolvable in the DAG; rework attempts < policy.rework.max_attempts | Orchestrator |
| `T-106` | `TECHNICAL_QA` | `REWORK` | `defect_detected` | ≥1 finding with severity ≥ policy.rework.min_severity; responsible artifact resolvable in the DAG; rework attempts < policy.rework.max_attempts | Orchestrator |
| `T-107` | `VISUAL_QA` | `REWORK` | `defect_detected` | ≥1 finding with severity ≥ policy.rework.min_severity; responsible artifact resolvable in the DAG; rework attempts < policy.rework.max_attempts | Orchestrator |
| `T-108` | `AUDIO_QA` | `REWORK` | `defect_detected` | ≥1 finding with severity ≥ policy.rework.min_severity; responsible artifact resolvable in the DAG; rework attempts < policy.rework.max_attempts | Orchestrator |
| `T-109` | `CONTENT_QA` | `REWORK` | `defect_detected` | ≥1 finding with severity ≥ policy.rework.min_severity; responsible artifact resolvable in the DAG; rework attempts < policy.rework.max_attempts | Orchestrator |
| `T-110` | `RETENTION_QA` | `REWORK` | `defect_detected` | ≥1 finding with severity ≥ policy.rework.min_severity; responsible artifact resolvable in the DAG; rework attempts < policy.rework.max_attempts | Orchestrator |
| `T-111` | `COMPLIANCE_QA` | `REWORK` | `defect_detected` | ≥1 finding with severity ≥ policy.rework.min_severity; responsible artifact resolvable in the DAG; rework attempts < policy.rework.max_attempts | Orchestrator |
| `T-112` | `SCORING` | `REWORK` | `defect_detected` | ≥1 finding with severity ≥ policy.rework.min_severity; responsible artifact resolvable in the DAG; rework attempts < policy.rework.max_attempts | Orchestrator |
| `T-113` | `READY_TO_PUBLISH` | `REWORK` | `defect_detected` | ≥1 finding with severity ≥ policy.rework.min_severity; responsible artifact resolvable in the DAG; rework attempts < policy.rework.max_attempts | Orchestrator |
| `T-114` | `PUBLICATION_PROCESSING` | `REWORK` | `defect_detected` | ≥1 finding with severity ≥ policy.rework.min_severity; responsible artifact resolvable in the DAG; rework attempts < policy.rework.max_attempts | Orchestrator |
| `T-201` | `REWORK` | `RESEARCHING` | `regenerate_node` | Target is the earliest repairable ancestor of the responsible artifact; rework budget reserved; descendants marked SUPERSEDED | Orchestrator |
| `T-202` | `REWORK` | `SCRIPTING` | `regenerate_node` | Target is the earliest repairable ancestor of the responsible artifact; rework budget reserved; descendants marked SUPERSEDED | Orchestrator |
| `T-203` | `REWORK` | `STORYBOARDING` | `regenerate_node` | Target is the earliest repairable ancestor of the responsible artifact; rework budget reserved; descendants marked SUPERSEDED | Orchestrator |
| `T-204` | `REWORK` | `ASSET_GENERATION` | `regenerate_node` | Target is the earliest repairable ancestor of the responsible artifact; rework budget reserved; descendants marked SUPERSEDED | Orchestrator |
| `T-205` | `REWORK` | `AUDIO_GENERATION` | `regenerate_node` | Target is the earliest repairable ancestor of the responsible artifact; rework budget reserved; descendants marked SUPERSEDED | Orchestrator |
| `T-206` | `REWORK` | `EDITING` | `regenerate_node` | Target is the earliest repairable ancestor of the responsible artifact; rework budget reserved; descendants marked SUPERSEDED | Orchestrator |
| `T-2A1` | `REWORK` | `FAILED` | `rework_exhausted` | rework attempts = policy.rework.max_attempts OR identical failure signature repeated ≥ 2 times | Orchestrator |
| `T-2A2` | `REWORK` | `BLOCKED` | `rework_budget_exhausted` | Rework budget reservation refused; requires authorised budget change | Orchestrator |
| `T-030` | `RESEARCHING` | `BLOCKED` | `insufficient_evidence` | Evidence threshold unmet after policy.research.max_attempts; requires operator decision | Orchestrator |
| `T-031` | `PUBLISHING` | `FAILED` | `definitive_rejection` | Every target returned a non-retryable rejection with authoritative evidence | Orchestrator |
| `T-032` | `PUBLICATION_PROCESSING` | `FAILED` | `processing_rejected` | Every target reported terminal platform-side failure | Orchestrator |
| `T-301` | `INIT` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-302` | `RESEARCHING` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-303` | `RESEARCH_VERIFIED` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-304` | `CONCEPT_SELECTED` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-305` | `SCRIPTING` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-306` | `SCRIPT_VERIFIED` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-307` | `STORYBOARDING` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-308` | `STORYBOARD_VERIFIED` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-309` | `ASSET_GENERATION` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-310` | `ASSETS_READY` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-311` | `AUDIO_GENERATION` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-312` | `AUDIO_READY` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-313` | `EDITING` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-314` | `CANDIDATE_RENDERED` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-315` | `TECHNICAL_QA` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-316` | `VISUAL_QA` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-317` | `AUDIO_QA` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-318` | `CONTENT_QA` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-319` | `RETENTION_QA` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-320` | `COMPLIANCE_QA` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-321` | `SCORING` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-322` | `REWORK` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-323` | `FINAL_VERIFIED` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-324` | `READY_TO_PUBLISH` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-325` | `PUBLISHING` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-326` | `PUBLICATION_PROCESSING` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-327` | `PUBLICATION_VERIFIED` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-328` | `UNKNOWN_EXTERNAL_STATE` | `BLOCKED` | `policy_block` | Policy engine returns BLOCK (security, safety, rights, platform, budget, autonomy or kill switch); blocked_from is persisted | Orchestrator |
| `T-401` | `BLOCKED` | `INIT` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-402` | `BLOCKED` | `RESEARCHING` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-403` | `BLOCKED` | `RESEARCH_VERIFIED` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-404` | `BLOCKED` | `CONCEPT_SELECTED` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-405` | `BLOCKED` | `SCRIPTING` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-406` | `BLOCKED` | `SCRIPT_VERIFIED` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-407` | `BLOCKED` | `STORYBOARDING` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-408` | `BLOCKED` | `STORYBOARD_VERIFIED` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-409` | `BLOCKED` | `ASSET_GENERATION` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-410` | `BLOCKED` | `ASSETS_READY` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-411` | `BLOCKED` | `AUDIO_GENERATION` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-412` | `BLOCKED` | `AUDIO_READY` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-413` | `BLOCKED` | `EDITING` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-414` | `BLOCKED` | `CANDIDATE_RENDERED` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-415` | `BLOCKED` | `TECHNICAL_QA` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-416` | `BLOCKED` | `VISUAL_QA` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-417` | `BLOCKED` | `AUDIO_QA` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-418` | `BLOCKED` | `CONTENT_QA` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-419` | `BLOCKED` | `RETENTION_QA` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-420` | `BLOCKED` | `COMPLIANCE_QA` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-421` | `BLOCKED` | `SCORING` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-422` | `BLOCKED` | `REWORK` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-423` | `BLOCKED` | `FINAL_VERIFIED` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-424` | `BLOCKED` | `READY_TO_PUBLISH` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-425` | `BLOCKED` | `PUBLISHING` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-426` | `BLOCKED` | `PUBLICATION_PROCESSING` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-427` | `BLOCKED` | `PUBLICATION_VERIFIED` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-428` | `BLOCKED` | `UNKNOWN_EXTERNAL_STATE` | `resume` | target == productions.blocked_from AND blocking condition cleared AND authorised operator approval recorded | Operator |
| `T-4A1` | `BLOCKED` | `FAILED` | `abandon` | Operator abandons a blocked production | Operator |
| `T-4A2` | `BLOCKED` | `CANCELLED` | `cancel` | Operator cancels a blocked production | Operator |
| `T-501` | `RESEARCHING` | `UNKNOWN_EXTERNAL_STATE` | `ambiguous_side_effect` | External call was dispatched and no definitive response was obtained; intent row exists; unknown_from is persisted | Orchestrator |
| `T-502` | `SCRIPTING` | `UNKNOWN_EXTERNAL_STATE` | `ambiguous_side_effect` | External call was dispatched and no definitive response was obtained; intent row exists; unknown_from is persisted | Orchestrator |
| `T-503` | `STORYBOARDING` | `UNKNOWN_EXTERNAL_STATE` | `ambiguous_side_effect` | External call was dispatched and no definitive response was obtained; intent row exists; unknown_from is persisted | Orchestrator |
| `T-504` | `ASSET_GENERATION` | `UNKNOWN_EXTERNAL_STATE` | `ambiguous_side_effect` | External call was dispatched and no definitive response was obtained; intent row exists; unknown_from is persisted | Orchestrator |
| `T-505` | `AUDIO_GENERATION` | `UNKNOWN_EXTERNAL_STATE` | `ambiguous_side_effect` | External call was dispatched and no definitive response was obtained; intent row exists; unknown_from is persisted | Orchestrator |
| `T-506` | `EDITING` | `UNKNOWN_EXTERNAL_STATE` | `ambiguous_side_effect` | External call was dispatched and no definitive response was obtained; intent row exists; unknown_from is persisted | Orchestrator |
| `T-507` | `TECHNICAL_QA` | `UNKNOWN_EXTERNAL_STATE` | `ambiguous_side_effect` | External call was dispatched and no definitive response was obtained; intent row exists; unknown_from is persisted | Orchestrator |
| `T-508` | `VISUAL_QA` | `UNKNOWN_EXTERNAL_STATE` | `ambiguous_side_effect` | External call was dispatched and no definitive response was obtained; intent row exists; unknown_from is persisted | Orchestrator |
| `T-509` | `AUDIO_QA` | `UNKNOWN_EXTERNAL_STATE` | `ambiguous_side_effect` | External call was dispatched and no definitive response was obtained; intent row exists; unknown_from is persisted | Orchestrator |
| `T-510` | `CONTENT_QA` | `UNKNOWN_EXTERNAL_STATE` | `ambiguous_side_effect` | External call was dispatched and no definitive response was obtained; intent row exists; unknown_from is persisted | Orchestrator |
| `T-511` | `RETENTION_QA` | `UNKNOWN_EXTERNAL_STATE` | `ambiguous_side_effect` | External call was dispatched and no definitive response was obtained; intent row exists; unknown_from is persisted | Orchestrator |
| `T-512` | `COMPLIANCE_QA` | `UNKNOWN_EXTERNAL_STATE` | `ambiguous_side_effect` | External call was dispatched and no definitive response was obtained; intent row exists; unknown_from is persisted | Orchestrator |
| `T-513` | `PUBLISHING` | `UNKNOWN_EXTERNAL_STATE` | `ambiguous_side_effect` | External call was dispatched and no definitive response was obtained; intent row exists; unknown_from is persisted | Orchestrator |
| `T-514` | `PUBLICATION_PROCESSING` | `UNKNOWN_EXTERNAL_STATE` | `ambiguous_side_effect` | External call was dispatched and no definitive response was obtained; intent row exists; unknown_from is persisted | Orchestrator |
| `T-515` | `PUBLICATION_VERIFIED` | `UNKNOWN_EXTERNAL_STATE` | `ambiguous_side_effect` | External call was dispatched and no definitive response was obtained; intent row exists; unknown_from is persisted | Orchestrator |
| `T-601` | `UNKNOWN_EXTERNAL_STATE` | `RESEARCHING` | `reconciled_not_executed` | target == productions.unknown_from AND reconciliation proves the side effect did NOT take place | ReconciliationService |
| `T-602` | `UNKNOWN_EXTERNAL_STATE` | `SCRIPTING` | `reconciled_not_executed` | target == productions.unknown_from AND reconciliation proves the side effect did NOT take place | ReconciliationService |
| `T-603` | `UNKNOWN_EXTERNAL_STATE` | `STORYBOARDING` | `reconciled_not_executed` | target == productions.unknown_from AND reconciliation proves the side effect did NOT take place | ReconciliationService |
| `T-604` | `UNKNOWN_EXTERNAL_STATE` | `ASSET_GENERATION` | `reconciled_not_executed` | target == productions.unknown_from AND reconciliation proves the side effect did NOT take place | ReconciliationService |
| `T-605` | `UNKNOWN_EXTERNAL_STATE` | `AUDIO_GENERATION` | `reconciled_not_executed` | target == productions.unknown_from AND reconciliation proves the side effect did NOT take place | ReconciliationService |
| `T-606` | `UNKNOWN_EXTERNAL_STATE` | `EDITING` | `reconciled_not_executed` | target == productions.unknown_from AND reconciliation proves the side effect did NOT take place | ReconciliationService |
| `T-607` | `UNKNOWN_EXTERNAL_STATE` | `TECHNICAL_QA` | `reconciled_not_executed` | target == productions.unknown_from AND reconciliation proves the side effect did NOT take place | ReconciliationService |
| `T-608` | `UNKNOWN_EXTERNAL_STATE` | `VISUAL_QA` | `reconciled_not_executed` | target == productions.unknown_from AND reconciliation proves the side effect did NOT take place | ReconciliationService |
| `T-609` | `UNKNOWN_EXTERNAL_STATE` | `AUDIO_QA` | `reconciled_not_executed` | target == productions.unknown_from AND reconciliation proves the side effect did NOT take place | ReconciliationService |
| `T-610` | `UNKNOWN_EXTERNAL_STATE` | `CONTENT_QA` | `reconciled_not_executed` | target == productions.unknown_from AND reconciliation proves the side effect did NOT take place | ReconciliationService |
| `T-611` | `UNKNOWN_EXTERNAL_STATE` | `RETENTION_QA` | `reconciled_not_executed` | target == productions.unknown_from AND reconciliation proves the side effect did NOT take place | ReconciliationService |
| `T-612` | `UNKNOWN_EXTERNAL_STATE` | `COMPLIANCE_QA` | `reconciled_not_executed` | target == productions.unknown_from AND reconciliation proves the side effect did NOT take place | ReconciliationService |
| `T-613` | `UNKNOWN_EXTERNAL_STATE` | `PUBLISHING` | `reconciled_not_executed` | target == productions.unknown_from AND reconciliation proves the side effect did NOT take place | ReconciliationService |
| `T-614` | `UNKNOWN_EXTERNAL_STATE` | `PUBLICATION_PROCESSING` | `reconciled_not_executed` | target == productions.unknown_from AND reconciliation proves the side effect did NOT take place | ReconciliationService |
| `T-615` | `UNKNOWN_EXTERNAL_STATE` | `PUBLICATION_VERIFIED` | `reconciled_not_executed` | target == productions.unknown_from AND reconciliation proves the side effect did NOT take place | ReconciliationService |
| `T-6A1` | `UNKNOWN_EXTERNAL_STATE` | `PUBLICATION_PROCESSING` | `reconciled_accepted` | Reconciliation proves the upload was accepted; external_id recovered | ReconciliationService |
| `T-6A2` | `UNKNOWN_EXTERNAL_STATE` | `PUBLICATION_VERIFIED` | `reconciled_published` | Reconciliation retrieves authoritative published evidence (OFFICIAL_API/OFFICIAL_DASHBOARD/OPERATOR_CONFIRMATION only) for every required target | ReconciliationService |
| `T-6A3` | `UNKNOWN_EXTERNAL_STATE` | `BLOCKED` | `unreconcilable` | Reconciliation exhausted policy.reconcile.max_attempts without a definitive answer; requires operator | Orchestrator |
| `T-6A4` | `UNKNOWN_EXTERNAL_STATE` | `FAILED` | `reconciled_failed` | Reconciliation proves the operation failed definitively and is not repeatable | Orchestrator |
| `T-701` | `INIT` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-702` | `RESEARCHING` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-703` | `RESEARCH_VERIFIED` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-704` | `CONCEPT_SELECTED` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-705` | `SCRIPTING` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-706` | `SCRIPT_VERIFIED` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-707` | `STORYBOARDING` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-708` | `STORYBOARD_VERIFIED` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-709` | `ASSET_GENERATION` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-710` | `ASSETS_READY` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-711` | `AUDIO_GENERATION` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-712` | `AUDIO_READY` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-713` | `EDITING` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-714` | `CANDIDATE_RENDERED` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-715` | `TECHNICAL_QA` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-716` | `VISUAL_QA` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-717` | `AUDIO_QA` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-718` | `CONTENT_QA` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-719` | `RETENTION_QA` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-720` | `COMPLIANCE_QA` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-721` | `SCORING` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-722` | `REWORK` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-723` | `FINAL_VERIFIED` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-724` | `READY_TO_PUBLISH` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-725` | `PUBLISHING` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-726` | `PUBLICATION_PROCESSING` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-727` | `PUBLICATION_VERIFIED` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-728` | `UNKNOWN_EXTERNAL_STATE` | `CANCELLED` | `cancel` | Authorised operator cancel; no in-flight external intent is UNKNOWN; reservations released | Operator |
| `T-801` | `INIT` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-802` | `RESEARCHING` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-803` | `RESEARCH_VERIFIED` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-804` | `CONCEPT_SELECTED` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-805` | `SCRIPTING` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-806` | `SCRIPT_VERIFIED` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-807` | `STORYBOARDING` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-808` | `STORYBOARD_VERIFIED` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-809` | `ASSET_GENERATION` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-810` | `ASSETS_READY` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-811` | `AUDIO_GENERATION` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-812` | `AUDIO_READY` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-813` | `EDITING` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-814` | `CANDIDATE_RENDERED` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-815` | `TECHNICAL_QA` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-816` | `VISUAL_QA` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-817` | `AUDIO_QA` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-818` | `CONTENT_QA` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-819` | `RETENTION_QA` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-820` | `COMPLIANCE_QA` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-821` | `SCORING` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-822` | `FINAL_VERIFIED` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-823` | `READY_TO_PUBLISH` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-824` | `PUBLISHING` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-825` | `PUBLICATION_PROCESSING` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |
| `T-826` | `PUBLICATION_VERIFIED` | `FAILED` | `permanent_error` | Error classified PERMANENT by SPEC/05 taxonomy; retry budget exhausted; no ambiguous external intent outstanding | Orchestrator |

## Multi-target publication rollup

A production may target several platforms. The production-level publish states are a **rollup**
of the per-target `publications` rows, never a substitute for them. The rollup is deterministic:

| Rule | Definition |
|---|---|
| `R-1` | production.state = PUBLICATION_VERIFIED iff every publication row whose target is marked required has state = VERIFIED, established only via OFFICIAL_API, OFFICIAL_DASHBOARD or OPERATOR_CONFIRMATION evidence (V31-06). |
| `R-2` | production.state = PUBLICATION_PROCESSING iff at least one required publication is in {UPLOAD_REQUESTED, UPLOADED, PROCESSING, PUBLISHED} and none is in UNKNOWN_EXTERNAL_STATE. |
| `R-3` | production.state = UNKNOWN_EXTERNAL_STATE iff at least one required publication is in UNKNOWN_EXTERNAL_STATE. |
| `R-4` | production.state = FAILED via T-032 iff every required publication is in {FAILED, REJECTED}. |
| `R-5` | A partial outcome (some targets VERIFIED, some FAILED) holds the production in PUBLICATION_PROCESSING and raises an operator notification; it is never silently promoted to PUBLICATION_VERIFIED. |

## Persisted transition context

Three columns on `productions` exist solely to make the control transitions decidable:

- `blocked_from` — set when entering `BLOCKED`, cleared on resume. `T-4xx` is legal only when the
  target equals this value.
- `unknown_from` — set when entering `UNKNOWN_EXTERNAL_STATE`, cleared on reconciliation. `T-6xx` is
  legal only when the target equals this value.
- `rework_attempts` — bounded by `policy.rework.max_attempts`; `T-2A1` fires on exhaustion.

Without these columns the resume and reconcile transitions would be ambiguous, which is exactly
the defect this matrix exists to prevent.
