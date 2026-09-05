using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AMCCA.Core.Database;

public class MigrationService
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly string _workingDirectory;

    public MigrationService(DatabaseConnectionFactory connectionFactory, string? workingDirectory = null)
    {
        _connectionFactory = connectionFactory;
        _workingDirectory = workingDirectory ?? AppContext.BaseDirectory;
    }

    private static readonly List<(int Version, string Name, string UpSql, string DownSql)> BuiltInMigrations = new()
    {
        (
            1,
            "001_initial_core_schema",
            @"
                -- Settings and Kill Switch
                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value_json TEXT NOT NULL,
                    schema_version TEXT NOT NULL,
                    is_secret_ref INTEGER NOT NULL DEFAULT 0 CHECK(is_secret_ref IN (0,1)),
                    updated_at TEXT NOT NULL,
                    updated_by TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS kill_switch_state (
                    id INTEGER PRIMARY KEY CHECK(id=1),
                    mode TEXT NOT NULL CHECK(mode IN ('NORMAL','PAUSED','PUBLISHING_DISABLED','EMERGENCY_STOP')),
                    engaged_at TEXT NULL,
                    engaged_by TEXT NULL,
                    reason TEXT NULL,
                    cleared_at TEXT NULL,
                    cleared_by TEXT NULL
                );

                -- Events and Audit Log
                CREATE TABLE IF NOT EXISTS events (
                    event_id TEXT PRIMARY KEY,
                    event_type TEXT NOT NULL,
                    aggregate_type TEXT NOT NULL,
                    aggregate_id TEXT NOT NULL,
                    aggregate_version INTEGER NOT NULL,
                    correlation_id TEXT NOT NULL,
                    causation_id TEXT NULL,
                    transition_id TEXT NULL,
                    payload_json TEXT NOT NULL,
                    schema_version TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    seq INTEGER NOT NULL,
                    UNIQUE(aggregate_type, aggregate_id, aggregate_version)
                );

                CREATE TABLE IF NOT EXISTS audit_log (
                    audit_id TEXT PRIMARY KEY,
                    action TEXT NOT NULL,
                    actor_type TEXT NOT NULL CHECK(actor_type IN ('OPERATOR','SYSTEM')),
                    actor_id TEXT NOT NULL,
                    subject_type TEXT NULL,
                    subject_id TEXT NULL,
                    production_id TEXT NULL,
                    outcome TEXT NOT NULL,
                    policy_decision_id TEXT NULL,
                    reason_code TEXT NULL,
                    correlation_id TEXT NOT NULL,
                    schema_version TEXT NOT NULL,
                    occurred_at TEXT NOT NULL
                );

                -- Distribution & Synthetic Declarations (from SCHEMAS/schema.sql)
                CREATE TABLE IF NOT EXISTS synthetic_declarations (
                    id TEXT PRIMARY KEY
                );

                CREATE TABLE IF NOT EXISTS publications (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    platform TEXT NOT NULL,
                    account_id TEXT NOT NULL,
                    content_version_id TEXT NOT NULL,
                    metadata_version_id TEXT NULL,
                    referral_version_id TEXT NULL,
                    synthetic_declaration_id TEXT NULL,
                    platform_label_required INTEGER NOT NULL DEFAULT 0,
                    state TEXT NOT NULL,
                    required INTEGER NOT NULL DEFAULT 1,
                    idempotency_key TEXT NOT NULL,
                    provider_request_id TEXT NULL,
                    external_id TEXT NULL,
                    external_url TEXT NULL,
                    evidence_source TEXT NULL,
                    evidence_retrieved_at TEXT NULL,
                    synthetic_label_applied INTEGER NOT NULL DEFAULT 0,
                    attempt_count INTEGER NOT NULL DEFAULT 0,
                    last_error_code TEXT NULL,
                    schema_version TEXT NOT NULL DEFAULT '3.1.0',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY (synthetic_declaration_id) REFERENCES synthetic_declarations(id),
                    UNIQUE (idempotency_key),
                    UNIQUE (production_id, platform, account_id, content_version_id),
                    CHECK (state IN ('QUEUED', 'SUBMITTED', 'PROCESSING', 'PUBLISHED', 'VERIFIED', 'RECONCILING', 'FAILED', 'RETRACTED', 'UNKNOWN_EXTERNAL_STATE', 'INTENT_CREATED', 'UPLOAD_REQUESTED', 'UPLOADED', 'REJECTED', 'CANCELLED')),
                    CHECK (platform_label_required = 0 OR synthetic_declaration_id IS NOT NULL),
                    CHECK (state <> 'VERIFIED' OR (external_id IS NOT NULL
                           AND evidence_source IN ('OFFICIAL_API', 'OFFICIAL_DASHBOARD', 'OPERATOR_CONFIRMATION')
                           AND evidence_retrieved_at IS NOT NULL)),
                    CHECK (state <> 'VERIFIED' OR platform_label_required = 0 OR synthetic_label_applied = 1)
                );

                CREATE TABLE IF NOT EXISTS platform_capabilities (
                    platform TEXT NOT NULL,
                    account_id TEXT NOT NULL,
                    capability TEXT NOT NULL,
                    status TEXT NOT NULL,
                    evidence_source TEXT NOT NULL,
                    verified_at TEXT NOT NULL,
                    expires_at TEXT NULL,
                    PRIMARY KEY (platform, account_id, capability),
                    CHECK (status IN ('DISCOVERED', 'VERIFIED', 'UNVERIFIED', 'DISABLED', 'UNSUPPORTED')),
                    CHECK (status <> 'VERIFIED' OR evidence_source IN
                      ('OFFICIAL_API', 'OFFICIAL_DASHBOARD', 'OFFICIAL_DOCUMENTATION', 'DIRECT_PLATFORM_PROBE', 'OPERATOR_CONFIRMATION'))
                );
            ",
            @"
                DROP TABLE IF EXISTS platform_capabilities;
                DROP TABLE IF EXISTS publications;
                DROP TABLE IF EXISTS synthetic_declarations;
                DROP TABLE IF EXISTS audit_log;
                DROP TABLE IF EXISTS events;
                DROP TABLE IF EXISTS kill_switch_state;
                DROP TABLE IF EXISTS settings;
            "
        ),
        (
            2,
            "002_domain_policy_approvals_and_triggers",
            @"
                -- DEF-015: Events table append-only triggers
                CREATE TRIGGER IF NOT EXISTS trg_events_prevent_update
                BEFORE UPDATE ON events
                BEGIN
                    SELECT RAISE(ABORT, 'events table is strictly append-only; UPDATE is prohibited (D-001, DEF-015)');
                END;

                CREATE TRIGGER IF NOT EXISTS trg_events_prevent_delete
                BEFORE DELETE ON events
                BEGIN
                    SELECT RAISE(ABORT, 'events table is strictly append-only; DELETE is prohibited (D-001, DEF-015)');
                END;

                -- DEF-015: audit_log table append-only triggers
                CREATE TRIGGER IF NOT EXISTS trg_audit_log_prevent_update
                BEFORE UPDATE ON audit_log
                BEGIN
                    SELECT RAISE(ABORT, 'audit_log table is strictly append-only; UPDATE is prohibited (D-001, DEF-015)');
                END;

                CREATE TRIGGER IF NOT EXISTS trg_audit_log_prevent_delete
                BEFORE DELETE ON audit_log
                BEGIN
                    SELECT RAISE(ABORT, 'audit_log table is strictly append-only; DELETE is prohibited (D-001, DEF-015)');
                END;

                -- DEF-018: Full schema tables
                CREATE TABLE IF NOT EXISTS approvals (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    action TEXT NOT NULL,
                    scope_json TEXT NOT NULL,
                    state TEXT NOT NULL CHECK(state IN ('PENDING','APPROVED','REJECTED','EXPIRED','CONSUMED')),
                    single_use INTEGER NOT NULL DEFAULT 1,
                    decided_by TEXT NULL,
                    decided_at TEXT NULL,
                    consumed_at TEXT NULL,
                    expires_at TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS budgets (
                    id TEXT PRIMARY KEY,
                    window TEXT NOT NULL,
                    scope_id TEXT NOT NULL,
                    limit_amount TEXT NOT NULL CHECK(limit_amount NOT LIKE '-%'),
                    reserved TEXT NOT NULL DEFAULT '0.000000',
                    spent TEXT NOT NULL DEFAULT '0.000000',
                    currency TEXT NOT NULL DEFAULT 'EUR',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS policy_decisions (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    action TEXT NOT NULL,
                    decision TEXT NOT NULL CHECK(decision IN ('ALLOW','REQUIRE_APPROVAL','BLOCK')),
                    rule_key TEXT NOT NULL,
                    policy_version_id TEXT NOT NULL,
                    inputs_hash TEXT NOT NULL,
                    correlation_id TEXT NOT NULL,
                    decided_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS productions (
                    id TEXT PRIMARY KEY,
                    state TEXT NOT NULL,
                    blocked_from TEXT NULL,
                    unknown_from TEXT NULL,
                    rework_attempts INTEGER NOT NULL DEFAULT 0,
                    aggregate_version INTEGER NOT NULL DEFAULT 0,
                    autonomy_mode TEXT NOT NULL,
                    title TEXT NULL,
                    language TEXT NOT NULL,
                    niche_id TEXT NULL,
                    opportunity_id TEXT NULL,
                    current_manifest_id TEXT NULL,
                    schema_version TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS state_transitions (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    transition_id TEXT NOT NULL,
                    from_state TEXT NOT NULL,
                    to_state TEXT NOT NULL,
                    event_id TEXT NOT NULL,
                    actor_type TEXT NOT NULL,
                    correlation_id TEXT NOT NULL,
                    occurred_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS jobs (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NULL,
                    type TEXT NOT NULL,
                    state TEXT NOT NULL,
                    priority INTEGER NOT NULL DEFAULT 3,
                    idempotency_key TEXT NULL,
                    attempt INTEGER NOT NULL DEFAULT 0,
                    max_attempts INTEGER NOT NULL DEFAULT 3,
                    correlation_id TEXT NULL,
                    payload_json TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(idempotency_key)
                );

                CREATE TABLE IF NOT EXISTS leases (
                    job_id TEXT PRIMARY KEY,
                    owner_id TEXT NOT NULL,
                    acquired_at TEXT NOT NULL,
                    lease_until TEXT NOT NULL,
                    heartbeat_at TEXT NOT NULL,
                    fence_token INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS platform_accounts (
                    id TEXT PRIMARY KEY,
                    platform TEXT NOT NULL,
                    account_handle TEXT NOT NULL,
                    credential_secret_ref TEXT NOT NULL CHECK(credential_secret_ref LIKE 'secret://%'),
                    state TEXT NOT NULL CHECK(state IN ('DISCONNECTED','CONNECTED','REAUTH_REQUIRED','SUSPENDED','DISABLED')),
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS revenue_events (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NULL,
                    publication_id TEXT NULL,
                    program_id TEXT NULL,
                    state TEXT NOT NULL CHECK(state IN ('PENDING','CONFIRMED','DISPUTED','REVERSED')),
                    provenance TEXT NOT NULL CHECK(provenance IN ('OFFICIAL_API','STATEMENT_IMPORT','MANUAL_CONFIRMED')),
                    gross_amount TEXT NOT NULL,
                    fee_amount TEXT NOT NULL DEFAULT '0.000000',
                    net_amount TEXT NOT NULL,
                    currency TEXT NOT NULL,
                    statement_ref TEXT NULL,
                    occurred_at TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    CHECK(provenance <> 'ESTIMATED'),
                    CHECK(state = 'REVERSED' OR net_amount NOT LIKE '-%')
                );

                CREATE TABLE IF NOT EXISTS cost_events (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    job_id TEXT NULL,
                    kind TEXT NOT NULL CHECK(kind IN ('RESERVATION','SETTLEMENT','REFUND','ADJUSTMENT')),
                    amount TEXT NOT NULL,
                    currency TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    CHECK(kind = 'ADJUSTMENT' OR amount NOT LIKE '-%')
                );
            ",
            @"
                DROP TABLE IF EXISTS cost_events;
                DROP TABLE IF EXISTS revenue_events;
                DROP TABLE IF EXISTS platform_accounts;
                DROP TABLE IF EXISTS leases;
                DROP TABLE IF EXISTS jobs;
                DROP TABLE IF EXISTS state_transitions;
                DROP TABLE IF EXISTS productions;
                DROP TABLE IF EXISTS policy_decisions;
                DROP TABLE IF EXISTS budgets;
                DROP TABLE IF EXISTS approvals;
                DROP TRIGGER IF EXISTS trg_audit_log_prevent_delete;
                DROP TRIGGER IF EXISTS trg_audit_log_prevent_update;
                DROP TRIGGER IF EXISTS trg_events_prevent_delete;
                DROP TRIGGER IF EXISTS trg_events_prevent_update;
            "
        ),
        (
            3,
            "003_complete_canonical_schema",
            @"
                CREATE TABLE IF NOT EXISTS production_versions (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    version_no INTEGER NOT NULL,
                    manifest_id TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY(production_id) REFERENCES productions(id) ON DELETE RESTRICT,
                    UNIQUE(production_id, version_no)
                );

                CREATE TABLE IF NOT EXISTS artifacts (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    current_version_id TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(production_id) REFERENCES productions(id)
                );

                CREATE TABLE IF NOT EXISTS artifact_versions (
                    id TEXT PRIMARY KEY,
                    artifact_id TEXT NOT NULL,
                    version_no INTEGER NOT NULL,
                    sha256 TEXT NOT NULL,
                    bytes INTEGER NOT NULL,
                    rel_path TEXT NOT NULL,
                    state TEXT NOT NULL CHECK(state IN ('CURRENT','SUPERSEDED','INVALIDATED','TOMBSTONED')),
                    generator_model_id TEXT NULL,
                    prompt_version_id TEXT NULL,
                    rights_id TEXT NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY(artifact_id) REFERENCES artifacts(id),
                    UNIQUE(artifact_id, version_no),
                    CHECK(length(sha256)=64)
                );

                CREATE TABLE IF NOT EXISTS artifact_edges (
                    parent_version_id TEXT NOT NULL,
                    child_version_id TEXT NOT NULL,
                    edge_kind TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY(parent_version_id, child_version_id),
                    FOREIGN KEY(parent_version_id) REFERENCES artifact_versions(id),
                    FOREIGN KEY(child_version_id) REFERENCES artifact_versions(id)
                );

                CREATE TABLE IF NOT EXISTS artifact_manifests (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    sealed INTEGER NOT NULL DEFAULT 0,
                    manifest_sha256 TEXT NOT NULL,
                    schema_version TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY(production_id) REFERENCES productions(id),
                    CHECK(sealed IN (0,1))
                );

                CREATE TABLE IF NOT EXISTS job_attempts (
                    id TEXT PRIMARY KEY,
                    job_id TEXT NOT NULL,
                    attempt_no INTEGER NOT NULL,
                    worker_id TEXT NOT NULL,
                    outcome TEXT NOT NULL,
                    error_code TEXT NULL,
                    started_at TEXT NOT NULL,
                    finished_at TEXT NULL,
                    FOREIGN KEY(job_id) REFERENCES jobs(id),
                    UNIQUE(job_id, attempt_no)
                );

                CREATE TABLE IF NOT EXISTS intents (
                    id TEXT PRIMARY KEY,
                    job_id TEXT NULL,
                    production_id TEXT NULL,
                    kind TEXT NOT NULL,
                    target TEXT NOT NULL,
                    idempotency_key TEXT NOT NULL,
                    request_fingerprint TEXT NOT NULL,
                    state TEXT NOT NULL CHECK(state IN ('CREATED','DISPATCHED','CONFIRMED','REFUTED','UNKNOWN','ABANDONED')),
                    external_request_id TEXT NULL,
                    attempt_count INTEGER NOT NULL DEFAULT 0,
                    dispatched_at TEXT NULL,
                    resolved_at TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(idempotency_key)
                );

                CREATE TABLE IF NOT EXISTS reconciliation_attempts (
                    id TEXT PRIMARY KEY,
                    intent_id TEXT NOT NULL,
                    attempt_no INTEGER NOT NULL,
                    method TEXT NOT NULL,
                    outcome TEXT NOT NULL CHECK(outcome IN ('CONFIRMED','REFUTED','INCONCLUSIVE')),
                    evidence_ref TEXT NULL,
                    occurred_at TEXT NOT NULL,
                    FOREIGN KEY(intent_id) REFERENCES intents(id),
                    UNIQUE(intent_id, attempt_no)
                );

                CREATE TABLE IF NOT EXISTS prompt_templates (
                    id TEXT PRIMARY KEY,
                    key TEXT NOT NULL UNIQUE,
                    purpose TEXT NOT NULL,
                    current_version_id TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS prompt_versions (
                    id TEXT PRIMARY KEY,
                    template_id TEXT NOT NULL,
                    version_no INTEGER NOT NULL,
                    body_sha256 TEXT NOT NULL,
                    body_ref TEXT NOT NULL,
                    notes TEXT NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY(template_id) REFERENCES prompt_templates(id),
                    UNIQUE(template_id, version_no)
                );

                CREATE TABLE IF NOT EXISTS agent_runs (
                    run_id TEXT PRIMARY KEY,
                    production_id TEXT NULL,
                    job_id TEXT NULL,
                    agent_id TEXT NOT NULL,
                    agent_version TEXT NOT NULL,
                    prompt_version_id TEXT NOT NULL,
                    model_id TEXT NOT NULL,
                    model_params_hash TEXT NOT NULL,
                    state TEXT NOT NULL,
                    input_hash TEXT NOT NULL,
                    output_hash TEXT NULL,
                    output_valid INTEGER NULL,
                    provider_request_id TEXT NULL,
                    cost_event_id TEXT NULL,
                    correlation_id TEXT NOT NULL,
                    causation_id TEXT NULL,
                    schema_version TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    finished_at TEXT NULL,
                    FOREIGN KEY(prompt_version_id) REFERENCES prompt_versions(id)
                );

                CREATE TABLE IF NOT EXISTS tool_runs (
                    run_id TEXT PRIMARY KEY,
                    production_id TEXT NULL,
                    job_id TEXT NULL,
                    agent_run_id TEXT NULL,
                    tool_id TEXT NOT NULL,
                    tool_version TEXT NOT NULL,
                    side_effect_class TEXT NOT NULL,
                    state TEXT NOT NULL,
                    intent_id TEXT NULL,
                    idempotency_key TEXT NULL,
                    input_hash TEXT NOT NULL,
                    output_hash TEXT NULL,
                    correlation_id TEXT NOT NULL,
                    causation_id TEXT NULL,
                    schema_version TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    finished_at TEXT NULL,
                    FOREIGN KEY(intent_id) REFERENCES intents(id),
                    CHECK(side_effect_class<>'EXTERNAL_UNSAFE' OR intent_id IS NOT NULL)
                );

                CREATE TABLE IF NOT EXISTS agent_contracts (
                    id TEXT PRIMARY KEY,
                    agent_id TEXT NOT NULL,
                    agent_version TEXT NOT NULL,
                    input_schema_ref TEXT NOT NULL,
                    output_schema_ref TEXT NOT NULL,
                    allowed_tools_json TEXT NOT NULL,
                    forbidden_tools_json TEXT NOT NULL,
                    timeout_seconds INTEGER NOT NULL,
                    max_cost TEXT NOT NULL,
                    max_autonomy TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    UNIQUE(agent_id, agent_version)
                );

                CREATE TABLE IF NOT EXISTS sources (
                    id TEXT PRIMARY KEY,
                    url TEXT NOT NULL,
                    publisher TEXT NULL,
                    published_at TEXT NULL,
                    retrieved_at TEXT NOT NULL,
                    content_hash TEXT NOT NULL,
                    trust_tier TEXT NOT NULL CHECK(trust_tier IN ('PRIMARY','SECONDARY','AGGREGATOR','UNRATED')),
                    robots_allowed INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    UNIQUE(url, content_hash)
                );

                CREATE TABLE IF NOT EXISTS claims (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    text TEXT NOT NULL,
                    status TEXT NOT NULL CHECK(status IN ('VERIFIED','DISPUTED','ESTIMATED','UNKNOWN')),
                    materiality TEXT NOT NULL,
                    subject_class TEXT NOT NULL,
                    contains_personal_data INTEGER NOT NULL DEFAULT 0,
                    schema_version TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY(production_id) REFERENCES productions(id)
                );

                CREATE TABLE IF NOT EXISTS claim_sources (
                    claim_id TEXT NOT NULL,
                    source_id TEXT NOT NULL,
                    relation TEXT NOT NULL CHECK(relation IN ('SUPPORTS','CONTRADICTS','CONTEXT')),
                    excerpt_hash TEXT NULL,
                    PRIMARY KEY(claim_id, source_id)
                );

                CREATE TABLE IF NOT EXISTS rights_records (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    asset_hash TEXT NOT NULL,
                    status TEXT NOT NULL CHECK(status IN ('GREEN','YELLOW','RED')),
                    license TEXT NOT NULL,
                    provenance TEXT NOT NULL,
                    generator_model_id TEXT NULL,
                    author TEXT NULL,
                    acquired_at TEXT NULL,
                    expires_at TEXT NULL,
                    commercial_use TEXT NOT NULL,
                    modification TEXT NOT NULL,
                    attribution_required INTEGER NOT NULL,
                    attribution_text TEXT NULL,
                    restrictions_json TEXT NOT NULL,
                    evidence_ref TEXT NULL,
                    schema_version TEXT NOT NULL,
                    evaluated_at TEXT NOT NULL,
                    FOREIGN KEY(production_id) REFERENCES productions(id),
                    CHECK(status<>'GREEN' OR (commercial_use='ALLOWED' AND modification<>'UNKNOWN'))
                );

                CREATE TABLE IF NOT EXISTS qa_reports (
                    report_id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    artifact_version_id TEXT NOT NULL,
                    stage TEXT NOT NULL,
                    overall_score REAL NOT NULL,
                    critical_scores_json TEXT NOT NULL,
                    verdict TEXT NOT NULL CHECK(verdict IN ('PASS','FAIL')),
                    threshold_profile_id TEXT NOT NULL,
                    schema_version TEXT NOT NULL,
                    evaluated_at TEXT NOT NULL,
                    FOREIGN KEY(production_id) REFERENCES productions(id),
                    FOREIGN KEY(artifact_version_id) REFERENCES artifact_versions(id)
                );

                CREATE TABLE IF NOT EXISTS qa_findings (
                    id TEXT PRIMARY KEY,
                    report_id TEXT NOT NULL,
                    check_id TEXT NOT NULL,
                    check_kind TEXT NOT NULL CHECK(check_kind IN ('DETERMINISTIC','AI_ASSISTED')),
                    status TEXT NOT NULL,
                    severity TEXT NOT NULL,
                    responsible_artifact_version_id TEXT NOT NULL,
                    remediation_code TEXT NULL,
                    expected TEXT NULL,
                    actual TEXT NULL,
                    scene_ref TEXT NULL,
                    timecode_ms INTEGER NULL,
                    evidence_ref TEXT NULL,
                    message TEXT NULL,
                    FOREIGN KEY(report_id) REFERENCES qa_reports(report_id) ON DELETE CASCADE,
                    FOREIGN KEY(responsible_artifact_version_id) REFERENCES artifact_versions(id)
                );

                CREATE TABLE IF NOT EXISTS niches (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    language TEXT NOT NULL,
                    state TEXT NOT NULL CHECK(state IN ('CANDIDATE','TESTING','PROVEN','RETIRED')),
                    evidence_ref TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(name, language)
                );

                CREATE TABLE IF NOT EXISTS trends (
                    id TEXT PRIMARY KEY,
                    niche_id TEXT NULL,
                    label TEXT NOT NULL,
                    signal_strength REAL NOT NULL,
                    observed_at TEXT NOT NULL,
                    source_id TEXT NOT NULL,
                    expires_at TEXT NULL,
                    FOREIGN KEY(source_id) REFERENCES sources(id)
                );

                CREATE TABLE IF NOT EXISTS opportunities (
                    id TEXT PRIMARY KEY,
                    niche_id TEXT NOT NULL,
                    state TEXT NOT NULL CHECK(state IN ('NEW','SCORED','SELECTED','REJECTED','EXPIRED')),
                    score REAL NOT NULL,
                    score_breakdown_json TEXT NOT NULL,
                    expected_revenue TEXT NOT NULL CHECK(expected_revenue NOT LIKE '-%'),
                    expected_cost TEXT NOT NULL CHECK(expected_cost NOT LIKE '-%'),
                    risk_penalty REAL NOT NULL,
                    currency TEXT NOT NULL,
                    scored_at TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(niche_id) REFERENCES niches(id)
                );

                CREATE TABLE IF NOT EXISTS hooks (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NULL,
                    text TEXT NOT NULL,
                    pattern_id TEXT NULL,
                    measured_retention REAL NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY(production_id) REFERENCES productions(id)
                );

                CREATE TABLE IF NOT EXISTS publication_intents (
                    id TEXT PRIMARY KEY,
                    publication_id TEXT NOT NULL,
                    intent_id TEXT NOT NULL,
                    sequence_no INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY(publication_id) REFERENCES publications(id),
                    FOREIGN KEY(intent_id) REFERENCES intents(id),
                    UNIQUE(intent_id),
                    UNIQUE(publication_id, sequence_no)
                );

                CREATE TABLE IF NOT EXISTS publication_attempts (
                    id TEXT PRIMARY KEY,
                    publication_id TEXT NOT NULL,
                    attempt_no INTEGER NOT NULL,
                    outcome TEXT NOT NULL CHECK(outcome IN ('ACCEPTED','REJECTED','ERROR','UNKNOWN')),
                    http_status INTEGER NULL,
                    provider_request_id TEXT NULL,
                    error_code TEXT NULL,
                    started_at TEXT NOT NULL,
                    finished_at TEXT NULL,
                    FOREIGN KEY(publication_id) REFERENCES publications(id),
                    UNIQUE(publication_id, attempt_no)
                );

                CREATE TABLE IF NOT EXISTS budget_reservations (
                    id TEXT PRIMARY KEY,
                    budget_id TEXT NOT NULL,
                    production_id TEXT NULL,
                    job_id TEXT NULL,
                    amount TEXT NOT NULL CHECK(amount NOT LIKE '-%'),
                    state TEXT NOT NULL CHECK(state IN ('HELD','SETTLED','RELEASED','EXPIRED')),
                    expires_at TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(budget_id) REFERENCES budgets(id)
                );

                CREATE TABLE IF NOT EXISTS pricing_snapshots (
                    id TEXT PRIMARY KEY,
                    provider TEXT NOT NULL,
                    model_id TEXT NOT NULL,
                    unit TEXT NOT NULL,
                    unit_price TEXT NOT NULL CHECK(unit_price NOT LIKE '-%'),
                    currency TEXT NOT NULL,
                    effective_at TEXT NOT NULL,
                    retrieved_at TEXT NOT NULL,
                    source_ref TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    UNIQUE(provider, model_id, unit, effective_at)
                );

                CREATE TABLE IF NOT EXISTS referral_programs (
                    id TEXT PRIMARY KEY,
                    brand TEXT NOT NULL,
                    program TEXT NOT NULL,
                    state TEXT NOT NULL,
                    commission_model TEXT NULL,
                    disclosure_required INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(brand, program)
                );

                CREATE TABLE IF NOT EXISTS referral_links (
                    id TEXT PRIMARY KEY,
                    program_id TEXT NOT NULL,
                    production_id TEXT NULL,
                    code TEXT NULL,
                    url TEXT NULL,
                    state TEXT NOT NULL CHECK(state IN ('ACTIVE','EXPIRED','BLOCKED','REVIEW','UNVERIFIED','DISCOVERED')),
                    validation_method TEXT NOT NULL,
                    validation_evidence_ref TEXT NULL,
                    validated_at TEXT NOT NULL,
                    expires_at TEXT NULL,
                    geo_json TEXT NOT NULL,
                    platform_json TEXT NOT NULL,
                    schema_version TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(program_id) REFERENCES referral_programs(id)
                );

                CREATE TABLE IF NOT EXISTS attribution_events (
                    id TEXT PRIMARY KEY,
                    publication_id TEXT NOT NULL,
                    referral_link_id TEXT NULL,
                    kind TEXT NOT NULL,
                    value REAL NULL,
                    provenance TEXT NOT NULL CHECK(provenance IN ('API_MEASURED','IMPORTED','ESTIMATED')),
                    occurred_at TEXT NOT NULL,
                    ingested_at TEXT NOT NULL,
                    FOREIGN KEY(publication_id) REFERENCES publications(id)
                );

                CREATE TABLE IF NOT EXISTS analytics_snapshots (
                    id TEXT PRIMARY KEY,
                    production_id TEXT NOT NULL,
                    publication_id TEXT NOT NULL,
                    metric TEXT NOT NULL,
                    value REAL NOT NULL,
                    unit TEXT NULL,
                    currency TEXT NULL,
                    provenance TEXT NOT NULL,
                    window_start TEXT NULL,
                    window_end TEXT NULL,
                    schema_version TEXT NOT NULL,
                    observed_at TEXT NOT NULL,
                    FOREIGN KEY(publication_id) REFERENCES publications(id),
                    UNIQUE(publication_id, metric, window_start, provenance)
                );

                CREATE TABLE IF NOT EXISTS experiments (
                    id TEXT PRIMARY KEY,
                    hypothesis TEXT NOT NULL,
                    state TEXT NOT NULL CHECK(state IN ('DRAFT','RUNNING','CONCLUDED','ABANDONED')),
                    metric TEXT NOT NULL,
                    min_sample INTEGER NOT NULL,
                    started_at TEXT NULL,
                    concluded_at TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS experiment_variants (
                    id TEXT PRIMARY KEY,
                    experiment_id TEXT NOT NULL,
                    label TEXT NOT NULL,
                    parameters_json TEXT NOT NULL,
                    production_id TEXT NULL,
                    result_json TEXT NULL,
                    FOREIGN KEY(experiment_id) REFERENCES experiments(id),
                    UNIQUE(experiment_id, label)
                );

                CREATE TABLE IF NOT EXISTS memory_records (
                    id TEXT PRIMARY KEY,
                    scope TEXT NOT NULL,
                    key TEXT NOT NULL,
                    value_json TEXT NOT NULL,
                    evidence_ref TEXT NULL,
                    confidence REAL NOT NULL,
                    schema_version TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(scope, key)
                );

                CREATE TABLE IF NOT EXISTS policies (
                    id TEXT PRIMARY KEY,
                    key TEXT NOT NULL UNIQUE,
                    current_version_id TEXT NULL,
                    description TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS policy_versions (
                    id TEXT PRIMARY KEY,
                    policy_id TEXT NOT NULL,
                    version_no INTEGER NOT NULL,
                    body_sha256 TEXT NOT NULL,
                    body_ref TEXT NOT NULL,
                    activated_at TEXT NULL,
                    activated_by TEXT NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY(policy_id) REFERENCES policies(id),
                    UNIQUE(policy_id, version_no)
                );

                CREATE TABLE IF NOT EXISTS model_registry (
                    id TEXT PRIMARY KEY,
                    provider TEXT NOT NULL,
                    model_id TEXT NOT NULL,
                    capability TEXT NOT NULL,
                    protocol TEXT NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 0,
                    constraints_json TEXT NOT NULL,
                    pricing_snapshot_id TEXT NULL,
                    last_verified_at TEXT NULL,
                    fallback_order INTEGER NOT NULL DEFAULT 100,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(provider, model_id, capability),
                    CHECK(enabled = 0 OR last_verified_at IS NOT NULL)
                );

                CREATE TABLE IF NOT EXISTS provider_health (
                    id TEXT PRIMARY KEY,
                    provider TEXT NOT NULL,
                    window_start TEXT NOT NULL,
                    success_count INTEGER NOT NULL,
                    failure_count INTEGER NOT NULL,
                    timeout_count INTEGER NOT NULL,
                    circuit_state TEXT NOT NULL CHECK(circuit_state IN ('CLOSED','HALF_OPEN','OPEN')),
                    opened_at TEXT NULL,
                    UNIQUE(provider, window_start)
                );

                CREATE TABLE IF NOT EXISTS notifications (
                    id TEXT PRIMARY KEY,
                    severity TEXT NOT NULL CHECK(severity IN ('INFO','WARNING','ERROR','CRITICAL')),
                    category TEXT NOT NULL,
                    title TEXT NOT NULL,
                    body TEXT NOT NULL,
                    production_id TEXT NULL,
                    acknowledged_at TEXT NULL,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS backups (
                    id TEXT PRIMARY KEY,
                    kind TEXT NOT NULL CHECK(kind IN ('PRE_MIGRATION','SCHEDULED','MANUAL','PRE_RESTORE')),
                    path TEXT NOT NULL,
                    sha256 TEXT NOT NULL,
                    bytes INTEGER NOT NULL,
                    schema_version_at_backup TEXT NOT NULL,
                    verified INTEGER NOT NULL DEFAULT 0,
                    verified_at TEXT NULL,
                    created_at TEXT NOT NULL,
                    CHECK(verified IN (0,1))
                );
            ",
            @"
                DROP TABLE IF EXISTS backups;
                DROP TABLE IF EXISTS notifications;
                DROP TABLE IF EXISTS provider_health;
                DROP TABLE IF EXISTS model_registry;
                DROP TABLE IF EXISTS policy_versions;
                DROP TABLE IF EXISTS policies;
                DROP TABLE IF EXISTS memory_records;
                DROP TABLE IF EXISTS experiment_variants;
                DROP TABLE IF EXISTS experiments;
                DROP TABLE IF EXISTS analytics_snapshots;
                DROP TABLE IF EXISTS attribution_events;
                DROP TABLE IF EXISTS referral_links;
                DROP TABLE IF EXISTS referral_programs;
                DROP TABLE IF EXISTS pricing_snapshots;
                DROP TABLE IF EXISTS budget_reservations;
                DROP TABLE IF EXISTS publication_attempts;
                DROP TABLE IF EXISTS publication_intents;
                DROP TABLE IF EXISTS hooks;
                DROP TABLE IF EXISTS opportunities;
                DROP TABLE IF EXISTS trends;
                DROP TABLE IF EXISTS niches;
                DROP TABLE IF EXISTS qa_findings;
                DROP TABLE IF EXISTS qa_reports;
                DROP TABLE IF EXISTS rights_records;
                DROP TABLE IF EXISTS claim_sources;
                DROP TABLE IF EXISTS claims;
                DROP TABLE IF EXISTS sources;
                DROP TABLE IF EXISTS agent_contracts;
                DROP TABLE IF EXISTS tool_runs;
                DROP TABLE IF EXISTS agent_runs;
                DROP TABLE IF EXISTS prompt_versions;
                DROP TABLE IF EXISTS prompt_templates;
                DROP TABLE IF EXISTS reconciliation_attempts;
                DROP TABLE IF EXISTS intents;
                DROP TABLE IF EXISTS job_attempts;
                DROP TABLE IF EXISTS artifact_manifests;
                DROP TABLE IF EXISTS artifact_edges;
                DROP TABLE IF EXISTS artifact_versions;
                DROP TABLE IF EXISTS artifacts;
                DROP TABLE IF EXISTS production_versions;
            "
        ),
        (
            4,
            "004_audit_actor_types_and_tool_run_side_effect_check",
            @"
                -- I-09 / SPEC/55: audit_log's actor_type is deliberately narrower than every actor in the
                -- system -- it excludes AGENT so an agent can never be recorded as the authority for a
                -- protected action, not even by a bug. It was not meant to exclude the other legitimate
                -- non-agent actors that audit.schema.json already enumerates (SCHEDULER, ORCHESTRATOR,
                -- RECONCILER): the orchestrator is the sole state committer (DEF-008) and reconciliation
                -- (SPEC/18) writes decisions no code path currently audits, but both are real actors this
                -- schema must accept once they do. SQLite has no ALTER TABLE ... ADD CONSTRAINT, so the
                -- CHECK is widened by rebuilding the table; the append-only triggers are recreated after
                -- (DROP TABLE removes triggers bound to it).
                CREATE TABLE audit_log_new (
                    audit_id TEXT PRIMARY KEY,
                    action TEXT NOT NULL,
                    actor_type TEXT NOT NULL CHECK(actor_type IN ('OPERATOR','SCHEDULER','ORCHESTRATOR','RECONCILER','SYSTEM')),
                    actor_id TEXT NOT NULL,
                    subject_type TEXT NULL,
                    subject_id TEXT NULL,
                    production_id TEXT NULL,
                    outcome TEXT NOT NULL,
                    policy_decision_id TEXT NULL,
                    reason_code TEXT NULL,
                    correlation_id TEXT NOT NULL,
                    schema_version TEXT NOT NULL,
                    occurred_at TEXT NOT NULL
                );
                INSERT INTO audit_log_new SELECT * FROM audit_log;
                DROP TABLE audit_log;
                ALTER TABLE audit_log_new RENAME TO audit_log;

                CREATE TRIGGER trg_audit_log_prevent_update
                BEFORE UPDATE ON audit_log
                BEGIN
                    SELECT RAISE(ABORT, 'audit_log table is strictly append-only; UPDATE is prohibited (D-001, DEF-015)');
                END;

                CREATE TRIGGER trg_audit_log_prevent_delete
                BEFORE DELETE ON audit_log
                BEGIN
                    SELECT RAISE(ABORT, 'audit_log table is strictly append-only; DELETE is prohibited (D-001, DEF-015)');
                END;

                -- tool_run.schema.json bounds side_effect_class to five values, and the existing
                -- conditional CHECK on this table only holds if that domain is actually closed: an
                -- unconstrained column lets a mistyped value (e.g. lowercase or trailing whitespace)
                -- evade 'EXTERNAL_UNSAFE requires an intent_id' instead of being rejected by it -- the
                -- structural defence failing open rather than closed.
                CREATE TABLE tool_runs_new (
                    run_id TEXT PRIMARY KEY,
                    production_id TEXT NULL,
                    job_id TEXT NULL,
                    agent_run_id TEXT NULL,
                    tool_id TEXT NOT NULL,
                    tool_version TEXT NOT NULL,
                    side_effect_class TEXT NOT NULL CHECK(side_effect_class IN ('PURE','READ','LOCAL_WRITE','EXTERNAL_IDEMPOTENT','EXTERNAL_UNSAFE')),
                    state TEXT NOT NULL,
                    intent_id TEXT NULL,
                    idempotency_key TEXT NULL,
                    input_hash TEXT NOT NULL,
                    output_hash TEXT NULL,
                    correlation_id TEXT NOT NULL,
                    causation_id TEXT NULL,
                    schema_version TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    finished_at TEXT NULL,
                    FOREIGN KEY(intent_id) REFERENCES intents(id),
                    CHECK(side_effect_class<>'EXTERNAL_UNSAFE' OR intent_id IS NOT NULL)
                );
                INSERT INTO tool_runs_new SELECT * FROM tool_runs;
                DROP TABLE tool_runs;
                ALTER TABLE tool_runs_new RENAME TO tool_runs;
            ",
            @"
                CREATE TABLE tool_runs_old (
                    run_id TEXT PRIMARY KEY,
                    production_id TEXT NULL,
                    job_id TEXT NULL,
                    agent_run_id TEXT NULL,
                    tool_id TEXT NOT NULL,
                    tool_version TEXT NOT NULL,
                    side_effect_class TEXT NOT NULL,
                    state TEXT NOT NULL,
                    intent_id TEXT NULL,
                    idempotency_key TEXT NULL,
                    input_hash TEXT NOT NULL,
                    output_hash TEXT NULL,
                    correlation_id TEXT NOT NULL,
                    causation_id TEXT NULL,
                    schema_version TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    finished_at TEXT NULL,
                    FOREIGN KEY(intent_id) REFERENCES intents(id),
                    CHECK(side_effect_class<>'EXTERNAL_UNSAFE' OR intent_id IS NOT NULL)
                );
                INSERT INTO tool_runs_old SELECT * FROM tool_runs;
                DROP TABLE tool_runs;
                ALTER TABLE tool_runs_old RENAME TO tool_runs;

                DROP TRIGGER IF EXISTS trg_audit_log_prevent_delete;
                DROP TRIGGER IF EXISTS trg_audit_log_prevent_update;

                CREATE TABLE audit_log_old (
                    audit_id TEXT PRIMARY KEY,
                    action TEXT NOT NULL,
                    actor_type TEXT NOT NULL CHECK(actor_type IN ('OPERATOR','SYSTEM')),
                    actor_id TEXT NOT NULL,
                    subject_type TEXT NULL,
                    subject_id TEXT NULL,
                    production_id TEXT NULL,
                    outcome TEXT NOT NULL,
                    policy_decision_id TEXT NULL,
                    reason_code TEXT NULL,
                    correlation_id TEXT NOT NULL,
                    schema_version TEXT NOT NULL,
                    occurred_at TEXT NOT NULL
                );
                INSERT INTO audit_log_old SELECT * FROM audit_log;
                DROP TABLE audit_log;
                ALTER TABLE audit_log_old RENAME TO audit_log;

                CREATE TRIGGER trg_audit_log_prevent_update
                BEFORE UPDATE ON audit_log
                BEGIN
                    SELECT RAISE(ABORT, 'audit_log table is strictly append-only; UPDATE is prohibited (D-001, DEF-015)');
                END;

                CREATE TRIGGER trg_audit_log_prevent_delete
                BEFORE DELETE ON audit_log
                BEGIN
                    SELECT RAISE(ABORT, 'audit_log table is strictly append-only; DELETE is prohibited (D-001, DEF-015)');
                END;
            "
        ),
        (
            5,
            "005_migrate_kill_switch_from_settings_table",
            @"
                -- DEF-001/DEF-003: SettingsViewModel used to persist the operator's kill switch as
                -- settings['kill_switch.global'] = a JSON object with an 'active' boolean. That path was
                -- replaced with OperatorControlService.ToggleGlobalKillSwitchAsync writing
                -- kill_switch_state directly -- the same table SPEC/49 preflight gate 10 reads -- but
                -- nothing carried an existing operator's choice across the cutover: an installation with
                -- the kill switch engaged would have silently lost that fact on upgrade, since the code
                -- that used to read it was deleted along with the code that used to write it. This
                -- migration is that carry-over, run once.
                --
                -- Guarded by 'NOT EXISTS (kill_switch_state row)' so it never overwrites a decision an
                -- operator has already made through the new path on this database. Exactly two literal
                -- value_json strings were ever written by the old code, differing only in true/false, so
                -- a LIKE match on that literal is precise; the pattern is built with char(34) for the
                -- quote character rather than embedding one literally, so this file has no C#
                -- verbatim-string-escaped quotes for TOOLS/validate_package.py's migration parser to trip
                -- over (it assumes none appear in these strings, and none did before this).
                INSERT INTO kill_switch_state (id, mode, engaged_at, engaged_by, reason)
                SELECT 1, 'EMERGENCY_STOP', updated_at, updated_by,
                       'Migrated from settings.kill_switch.global (migration 005)'
                FROM settings
                WHERE key = 'kill_switch.global'
                  AND value_json LIKE '%' || char(34) || 'active' || char(34) || ':true%'
                  AND NOT EXISTS (SELECT 1 FROM kill_switch_state WHERE id = 1);

                INSERT INTO kill_switch_state (id, mode, cleared_at, cleared_by)
                SELECT 1, 'NORMAL', updated_at, updated_by
                FROM settings
                WHERE key = 'kill_switch.global'
                  AND value_json LIKE '%' || char(34) || 'active' || char(34) || ':false%'
                  AND NOT EXISTS (SELECT 1 FROM kill_switch_state WHERE id = 1);

                DELETE FROM settings WHERE key = 'kill_switch.global';
            ",
            @"
                INSERT INTO settings (key, value_json, schema_version, updated_by, updated_at)
                SELECT 'kill_switch.global',
                       char(123) || char(34) || 'active' || char(34) || ':' ||
                           (CASE WHEN mode = 'EMERGENCY_STOP' THEN 'true' ELSE 'false' END) || char(125),
                       '3.1.0',
                       COALESCE(engaged_by, cleared_by, 'migration'),
                       COALESCE(engaged_at, cleared_at, datetime('now'))
                FROM kill_switch_state
                WHERE id = 1
                ON CONFLICT(key) DO UPDATE SET
                    value_json = excluded.value_json,
                    updated_by = excluded.updated_by,
                    updated_at = excluded.updated_at;
            "
        ),
        (
            6,
            "006_job_success_state_renamed_to_succeeded",
            @"
                -- job.schema.json has always enumerated 'SUCCEEDED' as the terminal success state; the
                -- code path that completes a job (JobManager.CompleteJobAsync/CompleteJobOrThrowAsync)
                -- wrote 'COMPLETED' instead, a contract/implementation contradiction the jobs.state column
                -- had no CHECK to catch (fourth audit, AUDIT/FOURTH_AUDIT_PROJECT_AND_SPEC.md section 2.3).
                -- The code now writes 'SUCCEEDED'; this migration renames the value on rows written before
                -- that change so a job that already finished successfully does not read as if it never did.
                UPDATE jobs SET state = 'SUCCEEDED' WHERE state = 'COMPLETED';
            ",
            @"
                UPDATE jobs SET state = 'COMPLETED' WHERE state = 'SUCCEEDED';
            "
        ),
        (
            7,
            "007_analytics_snapshot_source_account_column",
            @"
                -- analytics.schema.json has always declared the optional source_account_id, but the
                -- column never existed (fourth audit, AUDIT/FOURTH_AUDIT_PROJECT_AND_SPEC.md section 2.2
                -- and 2.4's gate check). analytics_snapshots has no writer anywhere in this codebase yet,
                -- so this is a pure additive fix with no existing rows to reconcile. Nullable and
                -- FK-checked against platform_accounts, matching the schema's own description ('ULID,
                -- generated locally... external identifiers are never primary keys', D-003).
                --
                -- referral.schema.json's brand/commission_model/disclosure_required were investigated
                -- alongside this and deliberately NOT added as new referral_links columns: they already
                -- exist on referral_programs (the table referral_links.program_id references), so adding
                -- them here would duplicate a compliance-critical field (disclosure_required) across two
                -- tables that could drift out of sync -- the same normalization this codebase already uses
                -- for jobs/leases. See _FIELD_HAS_NO_OWN_COLUMN_BY_DESIGN in TOOLS/validate_package.py.
                ALTER TABLE analytics_snapshots ADD COLUMN source_account_id TEXT NULL REFERENCES platform_accounts(id);
            ",
            @"
                ALTER TABLE analytics_snapshots DROP COLUMN source_account_id;
            "
        ),
        (
            8,
            "008_jobs_and_cost_events_schema_version_column",
            @"
                -- D-004: 'Every persisted contract object carries schema_version.' job.schema.json and
                -- cost-event.schema.json both require it, and generate_artifacts.py's own SPEC/11 model
                -- for both tables already listed a schema_version column -- but neither table's real DDL
                -- ever had one, and neither writer (JobManager.EnqueueJobAsync, RevenueService.
                -- RecordCostAsync) ever set it. SPEC/11 was accidentally aspirational, not descriptive,
                -- for this one column on these two tables (fourth audit, AUDIT/FOURTH_AUDIT_PROJECT_AND_
                -- SPEC.md section 2.2, and 2.4's contracts.fields_have_columns gate check).
                --
                -- DEFAULT '3.1.0' (not a bare NOT NULL) because, unlike migration 7's tables, jobs and
                -- cost_events have real writers and so may already hold rows in a deployed database;
                -- SQLite requires a default to add a NOT NULL column to a non-empty table, and every row
                -- ever written by this codebase was in fact written under schema 3.1.0.
                ALTER TABLE jobs ADD COLUMN schema_version TEXT NOT NULL DEFAULT '3.1.0';
                ALTER TABLE cost_events ADD COLUMN schema_version TEXT NOT NULL DEFAULT '3.1.0';
            ",
            @"
                ALTER TABLE jobs DROP COLUMN schema_version;
                ALTER TABLE cost_events DROP COLUMN schema_version;
            "
        )
    };

    public static string ComputeSha256Checksum(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Trim();
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<IReadOnlyList<MigrationRecord>> GetAppliedMigrationsAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        await EnsureMigrationTableExistsAsync(connection, ct);

        var list = await connection.QueryAsync<MigrationRecord>(
            "SELECT version, name, checksum, applied_at AS AppliedAt, applied_by AS AppliedBy, rollback_sql_ref AS RollbackSqlRef FROM schema_migrations ORDER BY version ASC;");

        return list.ToList();
    }

    public async Task<MigrationReport> UpgradeAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        await EnsureMigrationTableExistsAsync(connection, ct);

        var applied = (await GetAppliedMigrationsAsync(ct)).ToDictionary(m => m.Version);

        // Verify checksums of already-applied migrations
        foreach (var m in BuiltInMigrations)
        {
            if (applied.TryGetValue((long)m.Version, out var existing))
            {
                var currentChecksum = ComputeSha256Checksum(m.UpSql);
                if (!string.Equals(existing.Checksum, currentChecksum, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AmccaException(
                        AmccaErrors.Db002,
                        ErrorCategory.Internal,
                        $"Migration checksum mismatch on version {m.Version} ('{m.Name}'). Recorded: {existing.Checksum}, Shipped: {currentChecksum}. Startup aborted.");
                }
            }
        }

        int appliedCount = 0;
        foreach (var m in BuiltInMigrations.OrderBy(m => m.Version))
        {
            if (applied.ContainsKey((long)m.Version)) continue;

            using var tx = connection.BeginTransaction();
            try
            {
                await connection.ExecuteAsync(m.UpSql, transaction: tx);

                var checksum = ComputeSha256Checksum(m.UpSql);
                var now = DateTimeOffset.UtcNow.ToString("O");

                await connection.ExecuteAsync(@"
                    INSERT INTO schema_migrations (version, name, checksum, applied_at, applied_by, rollback_sql_ref)
                    VALUES (@Version, @Name, @Checksum, @AppliedAt, @AppliedBy, @RollbackSqlRef);
                ", new
                {
                    m.Version,
                    m.Name,
                    Checksum = checksum,
                    AppliedAt = now,
                    AppliedBy = "AMCCA.Migrator",
                    RollbackSqlRef = m.Name + "_rollback"
                }, transaction: tx);

                tx.Commit();
                appliedCount++;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        return new MigrationReport(appliedCount, $"Applied {appliedCount} migration(s).");
    }

    public async Task<RollbackReport> RollbackAsync(int targetVersion, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        await EnsureMigrationTableExistsAsync(connection, ct);

        var applied = await GetAppliedMigrationsAsync(ct);
        var toRollback = applied
            .Where(m => m.Version > targetVersion)
            .OrderByDescending(m => m.Version)
            .ToList();

        int rolledBackCount = 0;
        foreach (var rec in toRollback)
        {
            var migrationDef = BuiltInMigrations.FirstOrDefault(m => m.Version == rec.Version);
            if (migrationDef == default)
            {
                throw new InvalidOperationException($"No rollback definition for version {rec.Version}.");
            }

            using var tx = connection.BeginTransaction();
            try
            {
                await connection.ExecuteAsync(migrationDef.DownSql, transaction: tx);
                await connection.ExecuteAsync(
                    "DELETE FROM schema_migrations WHERE version = @Version;",
                    new { rec.Version },
                    transaction: tx);

                tx.Commit();
                rolledBackCount++;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        return new RollbackReport(rolledBackCount, $"Rolled back {rolledBackCount} migration(s).");
    }

    private static async Task EnsureMigrationTableExistsAsync(SqliteConnection connection, CancellationToken ct)
    {
        const string ddl = @"
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                checksum TEXT NOT NULL,
                applied_at TEXT NOT NULL,
                applied_by TEXT NOT NULL,
                rollback_sql_ref TEXT NULL
            );
        ";
        using var cmd = connection.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
