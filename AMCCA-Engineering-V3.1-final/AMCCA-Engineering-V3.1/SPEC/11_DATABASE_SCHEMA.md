# 11 — Database Schema Contract

> **Generated artifact.** Emitted from `TOOLS/generate_artifacts.py`. `--check` diffs this file
> byte-for-byte against a fresh generation and fails the release gate on any difference (V31-01).
> `SPEC/10` defines the engine and transaction rules; this file defines every table.

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless documented exception; MAY = optional.

**Tables: 58.** There is no table declared anywhere in this package without a column
contract below; the validator fails the build otherwise.

## Conventions

- All primary identifiers are **ULID** strings generated locally. External identifiers are never primary keys (D-003).
- All timestamps are **TEXT** in RFC 3339 UTC with explicit offset, validated as `date-time` by `FormatChecker` at every JSON Schema boundary, not merely declared (V31-02).
- Monetary values are **TEXT decimal strings** with six fractional digits (D-023). Most are **NonNegativeMoney**; only `cost_events.amount` (kind=ADJUSTMENT) and `revenue_events.amount` (state=REVERSED) may be signed (V31-04). `REAL` is never used for money.
- All booleans are `INTEGER` constrained by `CHECK(col IN (0,1))`.
- Every table carrying a persisted contract object has a `schema_version` column (D-004).
- `foreign_keys=ON` is asserted on every connection; a connection that reports it off aborts startup.

## Table index

| Family | Tables |
|---|---|
| Migration and settings | `schema_migrations`, `settings`, `kill_switch_state` |
| Production core | `productions`, `production_versions`, `state_transitions` |
| Artifacts and lineage | `artifacts`, `artifact_versions`, `artifact_edges`, `artifact_manifests` |
| Durable execution | `jobs`, `job_attempts`, `leases`, `intents`, `reconciliation_attempts` |
| Events and audit | `events`, `audit_log` |
| Agents, tools, prompts | `agent_runs`, `tool_runs`, `prompt_templates`, `prompt_versions`, `agent_contracts` |
| Evidence plane | `sources`, `claims`, `claim_sources`, `rights_records`, `qa_reports`, `qa_findings` |
| Opportunity and strategy | `niches`, `trends`, `opportunities`, `hooks` |
| Distribution | `platform_accounts`, `platform_capabilities`, `publications`, `publication_intents`, `publication_attempts`, `synthetic_declarations` |
| Money | `budgets`, `budget_reservations`, `cost_events`, `pricing_snapshots`, `referral_programs`, `referral_links`, `attribution_events`, `revenue_events`, `analytics_snapshots` |
| Learning | `experiments`, `experiment_variants`, `memory_records` |
| Control plane | `policies`, `policy_versions`, `policy_decisions`, `approvals`, `model_registry`, `provider_health`, `notifications`, `backups` |

## Migration and settings

### `schema_migrations`

**Columns.** `version` INTEGER PK, `name` TEXT NOT NULL, `checksum` TEXT NOT NULL, `applied_at` TEXT NOT NULL, `applied_by` TEXT NOT NULL, `rollback_sql_ref` TEXT NULL

**Keys and constraints.** PK(version). UNIQUE(name). Applying a migration whose recorded checksum differs from the shipped file aborts startup with `AMCCA-DB-002`.

**Indexes.** none (small table, PK scan)

### `settings`

**Columns.** `key` TEXT PK, `value_json` TEXT NOT NULL, `schema_version` TEXT NOT NULL, `is_secret_ref` INTEGER NOT NULL DEFAULT 0, `updated_at` TEXT NOT NULL, `updated_by` TEXT NOT NULL

**Keys and constraints.** PK(key). CHECK(is_secret_ref IN (0,1)).

**Indexes.** none

### `kill_switch_state`

**Columns.** `id` INTEGER PK CHECK(id=1), `mode` TEXT NOT NULL, `engaged_at` TEXT NULL, `engaged_by` TEXT NULL, `reason` TEXT NULL, `cleared_at` TEXT NULL, `cleared_by` TEXT NULL

**Keys and constraints.** Single-row table. CHECK(mode IN ('NORMAL','PAUSED','PUBLISHING_DISABLED','EMERGENCY_STOP')).

**Indexes.** none

## Production core

### `productions`

**Columns.** `id` TEXT PK (ULID), `state` TEXT NOT NULL, `blocked_from` TEXT NULL, `unknown_from` TEXT NULL, `rework_attempts` INTEGER NOT NULL DEFAULT 0, `aggregate_version` INTEGER NOT NULL DEFAULT 0, `autonomy_mode` TEXT NOT NULL, `title` TEXT NULL, `language` TEXT NOT NULL, `niche_id` TEXT NULL, `opportunity_id` TEXT NULL, `current_manifest_id` TEXT NULL, `schema_version` TEXT NOT NULL, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** FK(niche_id)->niches, FK(opportunity_id)->opportunities, FK(current_manifest_id)->artifact_manifests. CHECK(state IN <32 canonical states>). CHECK(state<>'BLOCKED' OR blocked_from IS NOT NULL). CHECK(state<>'UNKNOWN_EXTERNAL_STATE' OR unknown_from IS NOT NULL).

**Indexes.** IX(state), IX(updated_at), IX(niche_id)

### `production_versions`

**Columns.** `id` TEXT PK (ULID), `production_id` TEXT NOT NULL, `version_no` INTEGER NOT NULL, `manifest_id` TEXT NOT NULL, `reason` TEXT NOT NULL, `created_at` TEXT NOT NULL

**Keys and constraints.** FK(production_id)->productions ON DELETE RESTRICT. UNIQUE(production_id, version_no).

**Indexes.** IX(production_id, version_no)

### `state_transitions`

**Columns.** `id` TEXT PK (ULID), `production_id` TEXT NOT NULL, `transition_id` TEXT NOT NULL, `from_state` TEXT NOT NULL, `to_state` TEXT NOT NULL, `event_id` TEXT NOT NULL, `actor_type` TEXT NOT NULL, `correlation_id` TEXT NOT NULL, `occurred_at` TEXT NOT NULL

**Keys and constraints.** FK(production_id)->productions, FK(event_id)->events. UNIQUE(event_id). `transition_id` MUST match an id in the canonical state model; a value outside that set is rejected with `AMCCA-STM-001`.

**Indexes.** IX(production_id, occurred_at), IX(transition_id)

## Artifacts and lineage

### `artifacts`

**Columns.** `id` TEXT PK (ULID), `production_id` TEXT NOT NULL, `kind` TEXT NOT NULL, `current_version_id` TEXT NULL, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** FK(production_id)->productions.

**Indexes.** IX(production_id, kind)

### `artifact_versions`

**Columns.** `id` TEXT PK (ULID), `artifact_id` TEXT NOT NULL, `version_no` INTEGER NOT NULL, `sha256` TEXT NOT NULL, `bytes` INTEGER NOT NULL, `rel_path` TEXT NOT NULL, `state` TEXT NOT NULL, `generator_model_id` TEXT NULL, `prompt_version_id` TEXT NULL, `rights_id` TEXT NULL, `created_at` TEXT NOT NULL

**Keys and constraints.** FK(artifact_id)->artifacts. UNIQUE(artifact_id, version_no). CHECK(state IN ('CURRENT','SUPERSEDED','INVALIDATED','TOMBSTONED')). CHECK(length(sha256)=64).

**Indexes.** UX(artifact_id, version_no), IX(sha256), IX(state)

### `artifact_edges`

**Columns.** `parent_version_id` TEXT NOT NULL, `child_version_id` TEXT NOT NULL, `edge_kind` TEXT NOT NULL, `created_at` TEXT NOT NULL

**Keys and constraints.** PK(parent_version_id, child_version_id). Both FKs -> artifact_versions.

**Indexes.** IX(child_version_id)

### `artifact_manifests`

**Columns.** `id` TEXT PK (ULID), `production_id` TEXT NOT NULL, `sealed` INTEGER NOT NULL DEFAULT 0, `manifest_sha256` TEXT NOT NULL, `schema_version` TEXT NOT NULL, `created_at` TEXT NOT NULL

**Keys and constraints.** FK(production_id)->productions. CHECK(sealed IN (0,1)).

**Indexes.** IX(production_id, created_at)

## Durable execution

### `jobs`

**Columns.** `id` TEXT PK (ULID), `production_id` TEXT NULL, `type` TEXT NOT NULL, `state` TEXT NOT NULL, `priority` INTEGER NOT NULL, `idempotency_key` TEXT NOT NULL, `attempt` INTEGER NOT NULL DEFAULT 0, `max_attempts` INTEGER NOT NULL, `scheduled_at` TEXT NULL, `deadline_at` TEXT NULL, `estimated_cost` TEXT NULL, `reserved_cost` TEXT NULL, `currency` TEXT NOT NULL, `correlation_id` TEXT NOT NULL, `causation_id` TEXT NULL, `last_error_code` TEXT NULL, `payload_json` TEXT NOT NULL, `schema_version` TEXT NOT NULL, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** UNIQUE(idempotency_key). CHECK(priority BETWEEN 0 AND 5). CHECK(state IN ('QUEUED','LEASED','RUNNING','SUCCEEDED','FAILED','BLOCKED','UNKNOWN_EXTERNAL_STATE','CANCELLED','DEAD_LETTER')). CHECK(estimated_cost IS NULL OR estimated_cost NOT LIKE '-%') and CHECK(reserved_cost IS NULL OR reserved_cost NOT LIKE '-%') — non-negative money enforced in storage too (V31-04).

**Indexes.** IX(state, priority, scheduled_at), IX(production_id), UX(idempotency_key)

### `job_attempts`

**Columns.** `id` TEXT PK (ULID), `job_id` TEXT NOT NULL, `attempt_no` INTEGER NOT NULL, `worker_id` TEXT NOT NULL, `outcome` TEXT NOT NULL, `error_code` TEXT NULL, `started_at` TEXT NOT NULL, `finished_at` TEXT NULL

**Keys and constraints.** FK(job_id)->jobs. UNIQUE(job_id, attempt_no).

**Indexes.** IX(job_id, attempt_no)

### `leases`

**Columns.** `job_id` TEXT PK, `owner_id` TEXT NOT NULL, `acquired_at` TEXT NOT NULL, `lease_until` TEXT NOT NULL, `heartbeat_at` TEXT NOT NULL, `fence_token` INTEGER NOT NULL

**Keys and constraints.** PK(job_id). FK(job_id)->jobs ON DELETE CASCADE. `fence_token` monotonically increases per acquisition.

**Indexes.** IX(lease_until)

### `intents`

**Columns.** `id` TEXT PK (ULID), `job_id` TEXT NULL, `production_id` TEXT NULL, `kind` TEXT NOT NULL, `target` TEXT NOT NULL, `idempotency_key` TEXT NOT NULL, `request_fingerprint` TEXT NOT NULL, `state` TEXT NOT NULL, `external_request_id` TEXT NULL, `attempt_count` INTEGER NOT NULL DEFAULT 0, `dispatched_at` TEXT NULL, `resolved_at` TEXT NULL, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** UNIQUE(idempotency_key). CHECK(state IN ('CREATED','DISPATCHED','CONFIRMED','REFUTED','UNKNOWN','ABANDONED')).

**Indexes.** IX(state), UX(idempotency_key), IX(production_id)

### `reconciliation_attempts`

**Columns.** `id` TEXT PK (ULID), `intent_id` TEXT NOT NULL, `attempt_no` INTEGER NOT NULL, `method` TEXT NOT NULL, `outcome` TEXT NOT NULL, `evidence_ref` TEXT NULL, `occurred_at` TEXT NOT NULL

**Keys and constraints.** FK(intent_id)->intents. UNIQUE(intent_id, attempt_no). CHECK(outcome IN ('CONFIRMED','REFUTED','INCONCLUSIVE')).

**Indexes.** IX(intent_id)

## Events and audit

### `events`

**Columns.** `event_id` TEXT PK, `event_type` TEXT NOT NULL, `aggregate_type` TEXT NOT NULL, `aggregate_id` TEXT NOT NULL, `aggregate_version` INTEGER NOT NULL, `correlation_id` TEXT NOT NULL, `causation_id` TEXT NULL, `transition_id` TEXT NULL, `payload_json` TEXT NOT NULL, `schema_version` TEXT NOT NULL, `occurred_at` TEXT NOT NULL, `seq` INTEGER NOT NULL

**Keys and constraints.** PK(event_id). UNIQUE(aggregate_type, aggregate_id, aggregate_version).

**Indexes.** IX(aggregate_type, aggregate_id, aggregate_version), IX(correlation_id), IX(occurred_at), IX(seq)

### `audit_log`

**Columns.** `audit_id` TEXT PK, `action` TEXT NOT NULL, `actor_type` TEXT NOT NULL, `actor_id` TEXT NOT NULL, `subject_type` TEXT NULL, `subject_id` TEXT NULL, `production_id` TEXT NULL, `outcome` TEXT NOT NULL, `policy_decision_id` TEXT NULL, `reason_code` TEXT NULL, `correlation_id` TEXT NOT NULL, `schema_version` TEXT NOT NULL, `occurred_at` TEXT NOT NULL

**Keys and constraints.** PK(audit_id). Physically separate from `events` (D-018).

**Indexes.** IX(occurred_at), IX(production_id), IX(correlation_id)

## Agents, tools, prompts

### `agent_runs`

**Columns.** `run_id` TEXT PK, `production_id` TEXT NULL, `job_id` TEXT NULL, `agent_id` TEXT NOT NULL, `agent_version` TEXT NOT NULL, `prompt_version_id` TEXT NOT NULL, `model_id` TEXT NOT NULL, `model_params_hash` TEXT NOT NULL, `state` TEXT NOT NULL, `input_hash` TEXT NOT NULL, `output_hash` TEXT NULL, `output_valid` INTEGER NULL, `provider_request_id` TEXT NULL, `cost_event_id` TEXT NULL, `correlation_id` TEXT NOT NULL, `causation_id` TEXT NULL, `schema_version` TEXT NOT NULL, `started_at` TEXT NOT NULL, `finished_at` TEXT NULL

**Keys and constraints.** FK(prompt_version_id)->prompt_versions.

**Indexes.** IX(production_id), IX(agent_id, started_at), IX(provider_request_id)

### `tool_runs`

**Columns.** `run_id` TEXT PK, `production_id` TEXT NULL, `job_id` TEXT NULL, `agent_run_id` TEXT NULL, `tool_id` TEXT NOT NULL, `tool_version` TEXT NOT NULL, `side_effect_class` TEXT NOT NULL, `state` TEXT NOT NULL, `intent_id` TEXT NULL, `idempotency_key` TEXT NULL, `input_hash` TEXT NOT NULL, `output_hash` TEXT NULL, `correlation_id` TEXT NOT NULL, `causation_id` TEXT NULL, `schema_version` TEXT NOT NULL, `started_at` TEXT NOT NULL, `finished_at` TEXT NULL

**Keys and constraints.** FK(intent_id)->intents. CHECK(side_effect_class<>'EXTERNAL_UNSAFE' OR intent_id IS NOT NULL) — invariant I-03 enforced in storage.

**Indexes.** IX(production_id), IX(tool_id, started_at)

### `prompt_templates`

**Columns.** `id` TEXT PK (ULID), `key` TEXT NOT NULL, `purpose` TEXT NOT NULL, `current_version_id` TEXT NULL, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** UNIQUE(key).

**Indexes.** UX(key)

### `prompt_versions`

**Columns.** `id` TEXT PK (ULID), `template_id` TEXT NOT NULL, `version_no` INTEGER NOT NULL, `body_sha256` TEXT NOT NULL, `body_ref` TEXT NOT NULL, `notes` TEXT NULL, `created_at` TEXT NOT NULL

**Keys and constraints.** FK(template_id)->prompt_templates. UNIQUE(template_id, version_no).

**Indexes.** UX(template_id, version_no), IX(body_sha256)

### `agent_contracts`

**Columns.** `id` TEXT PK (ULID), `agent_id` TEXT NOT NULL, `agent_version` TEXT NOT NULL, `input_schema_ref` TEXT NOT NULL, `output_schema_ref` TEXT NOT NULL, `allowed_tools_json` TEXT NOT NULL, `forbidden_tools_json` TEXT NOT NULL, `timeout_seconds` INTEGER NOT NULL, `max_cost` TEXT NOT NULL, `max_autonomy` TEXT NOT NULL, `created_at` TEXT NOT NULL

**Keys and constraints.** UNIQUE(agent_id, agent_version). `max_autonomy` caps the agent regardless of system autonomy mode.

**Indexes.** UX(agent_id, agent_version)

## Evidence plane

### `sources`

**Columns.** `id` TEXT PK (ULID), `url` TEXT NOT NULL, `publisher` TEXT NULL, `published_at` TEXT NULL, `retrieved_at` TEXT NOT NULL, `content_hash` TEXT NOT NULL, `trust_tier` TEXT NOT NULL, `robots_allowed` INTEGER NOT NULL, `created_at` TEXT NOT NULL

**Keys and constraints.** UNIQUE(url, content_hash). CHECK(trust_tier IN ('PRIMARY','SECONDARY','AGGREGATOR','UNRATED')).

**Indexes.** IX(retrieved_at), UX(url, content_hash)

### `claims`

**Columns.** `id` TEXT PK (ULID), `production_id` TEXT NOT NULL, `text` TEXT NOT NULL, `status` TEXT NOT NULL, `materiality` TEXT NOT NULL, `subject_class` TEXT NOT NULL, `contains_personal_data` INTEGER NOT NULL DEFAULT 0, `schema_version` TEXT NOT NULL, `created_at` TEXT NOT NULL

**Keys and constraints.** FK(production_id)->productions. CHECK(status IN ('VERIFIED','DISPUTED','ESTIMATED','UNKNOWN')).

**Indexes.** IX(production_id, status), IX(contains_personal_data)

### `claim_sources`

**Columns.** `claim_id` TEXT NOT NULL, `source_id` TEXT NOT NULL, `relation` TEXT NOT NULL, `excerpt_hash` TEXT NULL

**Keys and constraints.** PK(claim_id, source_id). CHECK(relation IN ('SUPPORTS','CONTRADICTS','CONTEXT')).

**Indexes.** IX(source_id)

### `rights_records`

**Columns.** `id` TEXT PK (ULID), `production_id` TEXT NOT NULL, `asset_hash` TEXT NOT NULL, `status` TEXT NOT NULL, `license` TEXT NOT NULL, `provenance` TEXT NOT NULL, `generator_model_id` TEXT NULL, `author` TEXT NULL, `acquired_at` TEXT NULL, `expires_at` TEXT NULL, `commercial_use` TEXT NOT NULL, `modification` TEXT NOT NULL, `attribution_required` INTEGER NOT NULL, `attribution_text` TEXT NULL, `restrictions_json` TEXT NOT NULL, `evidence_ref` TEXT NULL, `schema_version` TEXT NOT NULL, `evaluated_at` TEXT NOT NULL

**Keys and constraints.** FK(production_id)->productions. CHECK(status IN ('GREEN','YELLOW','RED')). CHECK(status<>'GREEN' OR (commercial_use='ALLOWED' AND modification<>'UNKNOWN')).

**Indexes.** IX(asset_hash), IX(production_id, status)

### `qa_reports`

**Columns.** `report_id` TEXT PK, `production_id` TEXT NOT NULL, `artifact_version_id` TEXT NOT NULL, `stage` TEXT NOT NULL, `overall_score` REAL NOT NULL, `critical_scores_json` TEXT NOT NULL, `verdict` TEXT NOT NULL, `threshold_profile_id` TEXT NOT NULL, `schema_version` TEXT NOT NULL, `evaluated_at` TEXT NOT NULL

**Keys and constraints.** FK(production_id)->productions, FK(artifact_version_id)->artifact_versions. CHECK(verdict IN ('PASS','FAIL')).

**Indexes.** IX(production_id, stage), IX(artifact_version_id)

### `qa_findings`

**Columns.** `id` TEXT PK (ULID), `report_id` TEXT NOT NULL, `check_id` TEXT NOT NULL, `check_kind` TEXT NOT NULL, `status` TEXT NOT NULL, `severity` TEXT NOT NULL, `responsible_artifact_version_id` TEXT NOT NULL, `remediation_code` TEXT NULL, `expected` TEXT NULL, `actual` TEXT NULL, `scene_ref` TEXT NULL, `timecode_ms` INTEGER NULL, `evidence_ref` TEXT NULL, `message` TEXT NULL

**Keys and constraints.** FK(report_id)->qa_reports ON DELETE CASCADE, FK(responsible_artifact_version_id)->artifact_versions. CHECK(check_kind IN ('DETERMINISTIC','AI_ASSISTED')).

**Indexes.** IX(report_id), IX(responsible_artifact_version_id), IX(severity)

## Opportunity and strategy

### `niches`

**Columns.** `id` TEXT PK (ULID), `name` TEXT NOT NULL, `language` TEXT NOT NULL, `state` TEXT NOT NULL, `evidence_ref` TEXT NULL, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** UNIQUE(name, language). CHECK(state IN ('CANDIDATE','TESTING','PROVEN','RETIRED')).

**Indexes.** UX(name, language), IX(state)

### `trends`

**Columns.** `id` TEXT PK (ULID), `niche_id` TEXT NULL, `label` TEXT NOT NULL, `signal_strength` REAL NOT NULL, `observed_at` TEXT NOT NULL, `source_id` TEXT NOT NULL, `expires_at` TEXT NULL

**Keys and constraints.** FK(source_id)->sources.

**Indexes.** IX(niche_id, observed_at), IX(expires_at)

### `opportunities`

**Columns.** `id` TEXT PK (ULID), `niche_id` TEXT NOT NULL, `state` TEXT NOT NULL, `score` REAL NOT NULL, `score_breakdown_json` TEXT NOT NULL, `expected_revenue` TEXT NOT NULL, `expected_cost` TEXT NOT NULL, `risk_penalty` REAL NOT NULL, `currency` TEXT NOT NULL, `scored_at` TEXT NOT NULL, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** FK(niche_id)->niches. CHECK(state IN ('NEW','SCORED','SELECTED','REJECTED','EXPIRED')). CHECK(expected_revenue NOT LIKE '-%') and CHECK(expected_cost NOT LIKE '-%') — non-negative money (V31-04).

**Indexes.** IX(state, score), IX(niche_id)

### `hooks`

**Columns.** `id` TEXT PK (ULID), `production_id` TEXT NULL, `text` TEXT NOT NULL, `pattern_id` TEXT NULL, `measured_retention` REAL NULL, `created_at` TEXT NOT NULL

**Keys and constraints.** FK(production_id)->productions.

**Indexes.** IX(pattern_id)

## Distribution

### `platform_accounts`

**Columns.** `id` TEXT PK (ULID), `platform` TEXT NOT NULL, `handle` TEXT NOT NULL, `state` TEXT NOT NULL, `credential_secret_ref` TEXT NOT NULL, `scopes_json` TEXT NOT NULL, `connected_at` TEXT NULL, `last_verified_at` TEXT NULL, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** UNIQUE(platform, handle). CHECK(state IN ('DISCONNECTED','CONNECTED','REAUTH_REQUIRED','SUSPENDED','DISABLED')). CHECK(credential_secret_ref LIKE 'secret://%').

**Indexes.** UX(platform, handle), IX(state)

### `platform_capabilities`

**Columns.** `platform` TEXT NOT NULL, `account_id` TEXT NOT NULL, `capability` TEXT NOT NULL, `status` TEXT NOT NULL, `evidence_source` TEXT NOT NULL, `verified_at` TEXT NOT NULL, `expires_at` TEXT NULL

**Keys and constraints.** PK(platform, account_id, capability). CHECK(status IN ('DISCOVERED', 'VERIFIED', 'UNVERIFIED', 'DISABLED', 'UNSUPPORTED')). CHECK(status<>'VERIFIED' OR evidence_source IN ('OFFICIAL_API', 'OFFICIAL_DASHBOARD', 'OFFICIAL_DOCUMENTATION', 'DIRECT_PLATFORM_PROBE', 'OPERATOR_CONFIRMATION')) — a secondary source (blog, agency article, community guide) can only ever produce DISCOVERED, never VERIFIED (V31-09).

**Indexes.** IX(account_id), IX(expires_at)

### `publications`

**Columns.** `id` TEXT PK (ULID), `production_id` TEXT NOT NULL, `platform` TEXT NOT NULL, `account_id` TEXT NOT NULL, `content_version_id` TEXT NOT NULL, `metadata_version_id` TEXT NULL, `referral_version_id` TEXT NULL, `synthetic_declaration_id` TEXT NULL, `platform_label_required` INTEGER NOT NULL DEFAULT 0, `state` TEXT NOT NULL, `required` INTEGER NOT NULL DEFAULT 1, `idempotency_key` TEXT NOT NULL, `external_id` TEXT NULL, `external_url` TEXT NULL, `evidence_source` TEXT NULL, `evidence_retrieved_at` TEXT NULL, `synthetic_label_applied` INTEGER NOT NULL DEFAULT 0, `attempt_count` INTEGER NOT NULL DEFAULT 0, `last_error_code` TEXT NULL, `schema_version` TEXT NOT NULL, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** FK(production_id)->productions, FK(account_id)->platform_accounts, FK(content_version_id)->artifact_versions, FK(synthetic_declaration_id)->synthetic_declarations (V31-07). UNIQUE(idempotency_key). UNIQUE(production_id, platform, account_id, content_version_id). CHECK(platform_label_required=0 OR synthetic_declaration_id IS NOT NULL) — a required label must be traced to its declaration. CHECK(state<>'VERIFIED' OR (external_id IS NOT NULL AND evidence_source IN ('OFFICIAL_API','OFFICIAL_DASHBOARD','OPERATOR_CONFIRMATION') AND evidence_retrieved_at IS NOT NULL)) — invariant I-11 tightened: POST_PUBLISH_CHECK (a resolving URL) cannot satisfy this CHECK (V31-06). CHECK(state<>'VERIFIED' OR platform_label_required=0 OR synthetic_label_applied=1) — invariant I-18 made structural (V31-07).

**Indexes.** UX(idempotency_key), UX(production_id, platform, account_id, content_version_id), IX(state), IX(synthetic_declaration_id)

### `publication_intents`

**Columns.** `id` TEXT PK (ULID), `publication_id` TEXT NOT NULL, `intent_id` TEXT NOT NULL, `sequence_no` INTEGER NOT NULL, `created_at` TEXT NOT NULL

**Keys and constraints.** FK(publication_id)->publications, FK(intent_id)->intents. UNIQUE(intent_id). UNIQUE(publication_id, sequence_no).

**Indexes.** IX(publication_id)

### `publication_attempts`

**Columns.** `id` TEXT PK (ULID), `publication_id` TEXT NOT NULL, `attempt_no` INTEGER NOT NULL, `outcome` TEXT NOT NULL, `http_status` INTEGER NULL, `provider_request_id` TEXT NULL, `error_code` TEXT NULL, `started_at` TEXT NOT NULL, `finished_at` TEXT NULL

**Keys and constraints.** FK(publication_id)->publications. UNIQUE(publication_id, attempt_no). CHECK(outcome IN ('ACCEPTED','REJECTED','ERROR','UNKNOWN')). An `http_status` of 200 recorded here does not by itself justify any state change (V31-06).

**Indexes.** IX(publication_id)

### `synthetic_declarations`

**Columns.** `id` TEXT PK (ULID), `production_id` TEXT NOT NULL, `publication_id` TEXT NULL, `generated_components_json` TEXT NOT NULL, `responsibility_json` TEXT NOT NULL, `platform_label_required` INTEGER NOT NULL, `platform_label_applied` INTEGER NOT NULL DEFAULT 0, `in_content_disclosure_text` TEXT NULL, `policy_basis` TEXT NOT NULL, `evaluated_at` TEXT NOT NULL

**Keys and constraints.** FK(production_id)->productions, FK(publication_id)->publications. `responsibility_json` records which obligation (provider machine-readable marking / deployer disclosure / platform-native label / C2PA provenance) is whose responsibility, per the matrix in SPEC/45 (V31-08). CHECK(platform_label_required=0 OR platform_label_applied=1 OR publication_id IS NULL).

**Indexes.** IX(production_id)

## Money

### `budgets`

**Columns.** `id` TEXT PK (ULID), `scope` TEXT NOT NULL, `window_start` TEXT NOT NULL, `window_end` TEXT NOT NULL, `limit_amount` TEXT NOT NULL, `currency` TEXT NOT NULL, `state` TEXT NOT NULL, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** UNIQUE(scope, window_start). CHECK(scope IN ('PRODUCTION','REWORK','RECOVERY','DAILY','MONTHLY')). CHECK(limit_amount NOT LIKE '-%') — a budget limit is NonNegativeMoney, never signed (V31-04).

**Indexes.** UX(scope, window_start), IX(state)

### `budget_reservations`

**Columns.** `id` TEXT PK (ULID), `budget_id` TEXT NOT NULL, `production_id` TEXT NULL, `job_id` TEXT NULL, `amount` TEXT NOT NULL, `state` TEXT NOT NULL, `expires_at` TEXT NOT NULL, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** FK(budget_id)->budgets. CHECK(state IN ('HELD','SETTLED','RELEASED','EXPIRED')). CHECK(amount NOT LIKE '-%').

**Indexes.** IX(budget_id, state), IX(expires_at)

### `cost_events`

**Columns.** `cost_event_id` TEXT PK, `production_id` TEXT NULL, `job_id` TEXT NULL, `agent_run_id` TEXT NULL, `kind` TEXT NOT NULL, `amount` TEXT NOT NULL, `currency` TEXT NOT NULL, `provider` TEXT NULL, `model_id` TEXT NULL, `provider_request_id` TEXT NULL, `units_json` TEXT NULL, `pricing_snapshot_id` TEXT NOT NULL, `reconciliation_state` TEXT NOT NULL, `budget_id` TEXT NULL, `schema_version` TEXT NOT NULL, `occurred_at` TEXT NOT NULL

**Keys and constraints.** FK(pricing_snapshot_id)->pricing_snapshots. CHECK(kind IN ('ESTIMATE','RESERVATION','SETTLEMENT','RELEASE','ADJUSTMENT')). CHECK(kind = 'ADJUSTMENT' OR amount NOT LIKE '-%') — only ADJUSTMENT may be signed (V31-04).

**Indexes.** IX(production_id), IX(reconciliation_state), IX(provider_request_id)

### `pricing_snapshots`

**Columns.** `id` TEXT PK (ULID), `provider` TEXT NOT NULL, `model_id` TEXT NOT NULL, `unit` TEXT NOT NULL, `unit_price` TEXT NOT NULL, `currency` TEXT NOT NULL, `effective_at` TEXT NOT NULL, `retrieved_at` TEXT NOT NULL, `source_ref` TEXT NOT NULL, `created_at` TEXT NOT NULL

**Keys and constraints.** UNIQUE(provider, model_id, unit, effective_at). Immutable.

**Indexes.** UX(provider, model_id, unit, effective_at)

### `referral_programs`

**Columns.** `id` TEXT PK (ULID), `brand` TEXT NOT NULL, `program` TEXT NOT NULL, `state` TEXT NOT NULL, `commission_model` TEXT NULL, `disclosure_required` INTEGER NOT NULL, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** UNIQUE(brand, program).

**Indexes.** UX(brand, program)

### `referral_links`

**Columns.** `id` TEXT PK (ULID), `program_id` TEXT NOT NULL, `production_id` TEXT NULL, `code` TEXT NULL, `url` TEXT NULL, `state` TEXT NOT NULL, `validation_method` TEXT NOT NULL, `validation_evidence_ref` TEXT NULL, `validated_at` TEXT NOT NULL, `expires_at` TEXT NULL, `geo_json` TEXT NOT NULL, `platform_json` TEXT NOT NULL, `schema_version` TEXT NOT NULL, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** FK(program_id)->referral_programs. CHECK(state IN ('ACTIVE','EXPIRED','BLOCKED','REVIEW','UNVERIFIED','DISCOVERED')). CHECK(state<>'ACTIVE' OR (validation_method<>'HTTP_CHECK' AND validation_evidence_ref IS NOT NULL)).

**Indexes.** IX(program_id, state), IX(expires_at)

### `attribution_events`

**Columns.** `id` TEXT PK (ULID), `publication_id` TEXT NOT NULL, `referral_link_id` TEXT NULL, `kind` TEXT NOT NULL, `value` REAL NULL, `provenance` TEXT NOT NULL, `occurred_at` TEXT NOT NULL, `ingested_at` TEXT NOT NULL

**Keys and constraints.** FK(publication_id)->publications. CHECK(provenance IN ('API_MEASURED','IMPORTED','ESTIMATED')).

**Indexes.** IX(publication_id, occurred_at)

### `revenue_events`

**Columns.** `id` TEXT PK (ULID), `publication_id` TEXT NULL, `referral_link_id` TEXT NULL, `amount` TEXT NOT NULL, `currency` TEXT NOT NULL, `state` TEXT NOT NULL, `provenance` TEXT NOT NULL, `external_ref` TEXT NULL, `occurred_at` TEXT NOT NULL, `confirmed_at` TEXT NULL

**Keys and constraints.** CHECK(state IN ('PENDING','CONFIRMED','REVERSED','ADJUSTED')). CHECK(provenance IN ('API_MEASURED','IMPORTED','OPERATOR_ENTERED')). CHECK(provenance<>'ESTIMATED'). CHECK(state='REVERSED' OR amount NOT LIKE '-%') — a REVERSED row alone may be signed (V31-04).

**Indexes.** IX(publication_id), IX(state, occurred_at)

### `analytics_snapshots`

**Columns.** `id` TEXT PK (ULID), `production_id` TEXT NOT NULL, `publication_id` TEXT NOT NULL, `metric` TEXT NOT NULL, `value` REAL NOT NULL, `unit` TEXT NULL, `currency` TEXT NULL, `provenance` TEXT NOT NULL, `window_start` TEXT NULL, `window_end` TEXT NULL, `schema_version` TEXT NOT NULL, `observed_at` TEXT NOT NULL

**Keys and constraints.** FK(publication_id)->publications. UNIQUE(publication_id, metric, window_start, provenance).

**Indexes.** IX(publication_id, metric), IX(observed_at)

## Learning

### `experiments`

**Columns.** `id` TEXT PK (ULID), `hypothesis` TEXT NOT NULL, `state` TEXT NOT NULL, `metric` TEXT NOT NULL, `min_sample` INTEGER NOT NULL, `started_at` TEXT NULL, `concluded_at` TEXT NULL, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** CHECK(state IN ('DRAFT','RUNNING','CONCLUDED','ABANDONED')).

**Indexes.** IX(state)

### `experiment_variants`

**Columns.** `id` TEXT PK (ULID), `experiment_id` TEXT NOT NULL, `label` TEXT NOT NULL, `parameters_json` TEXT NOT NULL, `production_id` TEXT NULL, `result_json` TEXT NULL

**Keys and constraints.** FK(experiment_id)->experiments. UNIQUE(experiment_id, label).

**Indexes.** UX(experiment_id, label)

### `memory_records`

**Columns.** `id` TEXT PK (ULID), `scope` TEXT NOT NULL, `key` TEXT NOT NULL, `value_json` TEXT NOT NULL, `evidence_ref` TEXT NULL, `confidence` REAL NOT NULL, `schema_version` TEXT NOT NULL, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** UNIQUE(scope, key).

**Indexes.** UX(scope, key)

## Control plane

### `policies`

**Columns.** `id` TEXT PK (ULID), `key` TEXT NOT NULL, `current_version_id` TEXT NULL, `description` TEXT NOT NULL, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** UNIQUE(key).

**Indexes.** UX(key)

### `policy_versions`

**Columns.** `id` TEXT PK (ULID), `policy_id` TEXT NOT NULL, `version_no` INTEGER NOT NULL, `body_sha256` TEXT NOT NULL, `body_ref` TEXT NOT NULL, `activated_at` TEXT NULL, `activated_by` TEXT NULL, `created_at` TEXT NOT NULL

**Keys and constraints.** FK(policy_id)->policies. UNIQUE(policy_id, version_no). Immutable.

**Indexes.** UX(policy_id, version_no)

### `policy_decisions`

**Columns.** `id` TEXT PK (ULID), `production_id` TEXT NULL, `action` TEXT NOT NULL, `decision` TEXT NOT NULL, `rule_key` TEXT NOT NULL, `policy_version_id` TEXT NOT NULL, `inputs_hash` TEXT NOT NULL, `correlation_id` TEXT NOT NULL, `decided_at` TEXT NOT NULL

**Keys and constraints.** FK(policy_version_id)->policy_versions. CHECK(decision IN ('ALLOW','REQUIRE_APPROVAL','BLOCK')).

**Indexes.** IX(production_id), IX(decided_at), IX(decision)

### `approvals`

**Columns.** `id` TEXT PK (ULID), `production_id` TEXT NULL, `action` TEXT NOT NULL, `scope_json` TEXT NOT NULL, `state` TEXT NOT NULL, `requested_at` TEXT NOT NULL, `decided_at` TEXT NULL, `decided_by` TEXT NULL, `expires_at` TEXT NOT NULL, `single_use` INTEGER NOT NULL DEFAULT 1, `consumed_at` TEXT NULL

**Keys and constraints.** CHECK(state IN ('PENDING','APPROVED','REJECTED','EXPIRED','CONSUMED')). CHECK(single_use IN (0,1)).

**Indexes.** IX(state, expires_at), IX(production_id)

### `model_registry`

**Columns.** `id` TEXT PK (ULID), `provider` TEXT NOT NULL, `model_id` TEXT NOT NULL, `capability` TEXT NOT NULL, `protocol` TEXT NOT NULL, `enabled` INTEGER NOT NULL DEFAULT 0, `constraints_json` TEXT NOT NULL, `pricing_snapshot_id` TEXT NULL, `last_verified_at` TEXT NULL, `fallback_order` INTEGER NOT NULL DEFAULT 100, `created_at` TEXT NOT NULL, `updated_at` TEXT NOT NULL

**Keys and constraints.** UNIQUE(provider, model_id, capability). CHECK(enabled=0 OR last_verified_at IS NOT NULL).

**Indexes.** UX(provider, model_id, capability), IX(enabled, fallback_order)

### `provider_health`

**Columns.** `id` TEXT PK (ULID), `provider` TEXT NOT NULL, `window_start` TEXT NOT NULL, `success_count` INTEGER NOT NULL, `failure_count` INTEGER NOT NULL, `timeout_count` INTEGER NOT NULL, `circuit_state` TEXT NOT NULL, `opened_at` TEXT NULL

**Keys and constraints.** UNIQUE(provider, window_start). CHECK(circuit_state IN ('CLOSED','HALF_OPEN','OPEN')).

**Indexes.** UX(provider, window_start)

### `notifications`

**Columns.** `id` TEXT PK (ULID), `severity` TEXT NOT NULL, `category` TEXT NOT NULL, `title` TEXT NOT NULL, `body` TEXT NOT NULL, `production_id` TEXT NULL, `acknowledged_at` TEXT NULL, `created_at` TEXT NOT NULL

**Keys and constraints.** CHECK(severity IN ('INFO','WARNING','ERROR','CRITICAL')).

**Indexes.** IX(acknowledged_at), IX(created_at)

### `backups`

**Columns.** `id` TEXT PK (ULID), `kind` TEXT NOT NULL, `path` TEXT NOT NULL, `sha256` TEXT NOT NULL, `bytes` INTEGER NOT NULL, `schema_version_at_backup` TEXT NOT NULL, `verified` INTEGER NOT NULL DEFAULT 0, `verified_at` TEXT NULL, `created_at` TEXT NOT NULL

**Keys and constraints.** CHECK(kind IN ('PRE_MIGRATION','SCHEDULED','MANUAL','PRE_RESTORE')). CHECK(verified IN (0,1)).

**Indexes.** IX(created_at), IX(verified)

## Transaction boundaries

These groupings are atomic. Each is one SQLite transaction; none of them contains a network call.

| # | Atomic unit | Rows written together |
|---|---|---|
| TX-1 | Commit a production state change | `productions` (state + aggregate_version), `state_transitions`, `events`, `audit_log` when the action was policy-gated |
| TX-2 | Claim a job | `leases` (insert with fence token), `jobs` (state -> LEASED), `job_attempts` |
| TX-3 | Reserve budget | `budgets` (conditional update), `budget_reservations`, `cost_events` (kind=RESERVATION) |
| TX-4 | Settle cost | `budget_reservations` (-> SETTLED), `budgets` (release remainder), `cost_events` (kind=SETTLEMENT) |
| TX-5 | Create an external intent | `intents` (state=CREATED), `tool_runs`, `events`. **Committed before the network call, never inside it.** |
| TX-6 | Record an external outcome | `intents` (-> CONFIRMED/REFUTED/UNKNOWN), `publication_attempts`, `publications`, `events` |
| TX-7 | Seal a manifest | `artifact_manifests` (sealed=1), `artifact_versions` (-> CURRENT/SUPERSEDED), `productions.current_manifest_id`, `events` |
| TX-8 | Persist a QA report | `qa_reports`, `qa_findings`, `events` |

## Concurrency rules

1. Job claiming is a single conditional `UPDATE`. Read-then-write claiming is forbidden and is covered by a concurrency test (SPEC/73).
2. Budget reservation is a single conditional `UPDATE` with the limit in the `WHERE` clause. The check and the write are the same statement, so two workers cannot both pass the check.
3. Aggregate writes use `aggregate_version` optimistic concurrency. The `UNIQUE(aggregate_type, aggregate_id, aggregate_version)` index on `events` turns a lost update into a constraint violation rather than silent corruption.
4. `busy_timeout` is configured; long transactions are forbidden. No transaction may span a network call, a file render or a user prompt.
5. `synchronous=NORMAL` in steady state; `FULL` around migrations, backups and exports.
