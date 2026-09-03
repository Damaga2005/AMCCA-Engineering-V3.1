# 11 — Traceability Map

> **Generated artifact.** Emitted from the real file listing by `TOOLS/generate_artifacts.py`.
> `--check` fails the build if any SPEC file is absent from this map, so it cannot silently fall
> out of date.

**SPEC documents: 83.** Numbering is contiguous 01-83 with no duplicates and no two documents
covering the same subject (D-022).

## Blueprint to SPEC

| Blueprint document | Detailed by |
|---|---|
| `BLUEPRINT/00_MASTER_BLUEPRINT.md` | README.md, DECISIONS.md, SYSTEM.md, ARCHITECTURE.md |
| `BLUEPRINT/01_SYSTEM_CONTEXT.md` | SPEC/23, SPEC/27, SPEC/41, SPEC/50 |
| `BLUEPRINT/02_COMPONENT_MAP.md` | SPEC/06, SPEC/07, SPEC/08, SPEC/14, SPEC/17, SPEC/33 |
| `BLUEPRINT/03_END_TO_END_RUNTIME.md` | SPEC/12, SPEC/13, SPEC/26, SPEC/32, SPEC/35, SPEC/44 |
| `BLUEPRINT/04_STATE_AND_DATAFLOW.md` | SPEC/10, SPEC/11, SPEC/18, SPEC/19, SPEC/47, SPEC/55 |
| `BLUEPRINT/05_AUTONOMY_POLICY_APPROVALS.md` | SPEC/08, SPEC/09, SPEC/53, POLICIES/AUTONOMY_POLICY.md |
| `BLUEPRINT/06_EXTERNAL_INTEGRATIONS.md` | SPEC/15, SPEC/23, SPEC/41, SPEC/42, SPEC/43 |
| `BLUEPRINT/07_FAILURE_RECOVERY_COST_STORAGE.md` | SPEC/05, SPEC/16, SPEC/20, SPEC/21, SPEC/52 |
| `BLUEPRINT/08_SECURITY_OBSERVABILITY_TESTING.md` | SPEC/28, SPEC/50, SPEC/54, SPEC/70, SPEC/72 |
| `BLUEPRINT/09_DEPLOYMENT_AND_UI.md` | SPEC/60, SPEC/61, SPEC/76, SPEC/77 |
| `BLUEPRINT/10_OPERATIONAL_INVARIANTS.md` | SPEC/71 (test matrix), TOOLS/validate_package.py, TOOLS/generate_artifacts.py |

## SPEC index by band

### 01-09 — Foundations

Stack, runtime, configuration, contracts, errors, agents, tools, policy, approvals.

| Document | Subject |
|---|---|
| `SPEC/01_TECH_STACK.md` | Tech Stack |
| `SPEC/02_RUNTIME.md` | Runtime |
| `SPEC/03_CONFIGURATION.md` | Configuration |
| `SPEC/04_CONTRACTS.md` | Contracts |
| `SPEC/05_ERROR_MODEL.md` | Error Model |
| `SPEC/06_AGENT_SYSTEM.md` | Agent System |
| `SPEC/07_TOOL_REGISTRY.md` | Tool Registry |
| `SPEC/08_POLICY_ENGINE.md` | Policy Engine |
| `SPEC/09_APPROVALS.md` | Approvals |

### 10-19 — Persistence and durable execution

Database, state machine, jobs, idempotency, recovery, scheduling, artifacts, storage.

| Document | Subject |
|---|---|
| `SPEC/10_DATABASE_ENGINE.md` | Database Engine |
| `SPEC/11_DATABASE_SCHEMA.md` | Database Schema |
| `SPEC/12_STATE_MACHINE.md` | State Machine |
| `SPEC/13_STATE_TRANSITION_MATRIX.md` | State Transition Matrix |
| `SPEC/14_JOB_SYSTEM.md` | Job System |
| `SPEC/15_IDEMPOTENCY.md` | Idempotency |
| `SPEC/16_RECOVERY_RECONCILIATION.md` | Recovery Reconciliation |
| `SPEC/17_SCHEDULER.md` | Scheduler |
| `SPEC/18_ARTIFACTS.md` | Artifacts |
| `SPEC/19_STORAGE.md` | Storage |

### 20-29 — Cost, intelligence and evidence

Budgets, pricing, memory, gateway, routing, health, research.

| Document | Subject |
|---|---|
| `SPEC/20_COST_ENGINE.md` | Cost Engine |
| `SPEC/21_PRICING_RECONCILIATION.md` | Pricing Reconciliation |
| `SPEC/22_MEMORY.md` | Memory |
| `SPEC/23_PROVIDER_GATEWAY.md` | Provider Gateway |
| `SPEC/24_MODEL_ROUTER.md` | Model Router |
| `SPEC/25_PROVIDER_HEALTH.md` | Provider Health |
| `SPEC/26_RESEARCH_ENGINE.md` | Research Engine |
| `SPEC/27_RESEARCH_CONTRACTS.md` | Research Contracts |
| `SPEC/28_RESEARCH_SOURCE_SECURITY.md` | Research Source Security |
| `SPEC/29_TREND_NICHE.md` | Trend Niche |

### 30-39 — Content production

Strategy, hooks, script, media, QA, rights, rework, prompts, localisation.

| Document | Subject |
|---|---|
| `SPEC/30_CONTENT_STRATEGY.md` | Content Strategy |
| `SPEC/31_HOOK_ENGINE.md` | Hook Engine |
| `SPEC/32_SCRIPT_STORYBOARD.md` | Script Storyboard |
| `SPEC/33_MEDIA_PIPELINE.md` | Media Pipeline |
| `SPEC/34_MEDIA_PROFILE.md` | Media Profile |
| `SPEC/35_QA_ENGINE.md` | Qa Engine |
| `SPEC/36_RIGHTS_DUPLICATES.md` | Rights Duplicates |
| `SPEC/37_DAG_REWORK.md` | Dag Rework |
| `SPEC/38_PROMPT_VERSIONING.md` | Prompt Versioning |
| `SPEC/39_LOCALIZATION.md` | Localization |

### 40-49 — Distribution and money

Platforms, OAuth, publishing, synthetic disclosure, referrals, analytics, preflight.

| Document | Subject |
|---|---|
| `SPEC/40_PLATFORM_HUB.md` | Platform Hub |
| `SPEC/41_PLATFORM_ADAPTERS.md` | Platform Adapters |
| `SPEC/42_PLATFORM_CAPABILITY_MATRIX.md` | Platform Capability Matrix |
| `SPEC/43_OAUTH.md` | Oauth |
| `SPEC/44_PUBLISHING.md` | Publishing |
| `SPEC/45_SYNTHETIC_CONTENT_DISCLOSURE.md` | Synthetic Content Disclosure |
| `SPEC/46_REFERRAL_MONETIZATION.md` | Referral Monetization |
| `SPEC/47_ATTRIBUTION_ANALYTICS.md` | Attribution Analytics |
| `SPEC/48_EXPERIMENTS_GENOME.md` | Experiments Genome |
| `SPEC/49_PRE_FLIGHT.md` | Pre Flight |

### 50-59 — Security, privacy and operations

Security, privacy, retention, kill switch, observability, events, backup, export, versioning, dependencies.

| Document | Subject |
|---|---|
| `SPEC/50_SECURITY.md` | Security |
| `SPEC/51_PRIVACY_DATA_PROTECTION.md` | Privacy Data Protection |
| `SPEC/52_DATA_RETENTION.md` | Data Retention |
| `SPEC/53_KILL_SWITCH_AUTONOMY.md` | Kill Switch Autonomy |
| `SPEC/54_OBSERVABILITY.md` | Observability |
| `SPEC/55_EVENT_AUDIT.md` | Event Audit |
| `SPEC/56_BACKUP_DR.md` | Backup Dr |
| `SPEC/57_EXPORT_IMPORT.md` | Export Import |
| `SPEC/58_SCHEMA_VERSIONING.md` | Schema Versioning |
| `SPEC/59_DEPENDENCY_POLICY.md` | Dependency Policy |

### 60-69 — Interface and internal boundaries

UI, flows, state, notifications, internal API, optional HTTP boundary, performance, concurrency, time, diagnostics.

| Document | Subject |
|---|---|
| `SPEC/60_DESKTOP_UI.md` | Desktop Ui |
| `SPEC/61_UI_FLOWS.md` | Ui Flows |
| `SPEC/62_UI_STATE.md` | Ui State |
| `SPEC/63_NOTIFICATIONS.md` | Notifications |
| `SPEC/64_INTERNAL_API.md` | Internal Api |
| `SPEC/65_OPENAPI_BOUNDARY.md` | Openapi Boundary |
| `SPEC/66_PERFORMANCE.md` | Performance |
| `SPEC/67_CONCURRENCY_MODEL.md` | Concurrency Model |
| `SPEC/68_TIME_AND_CLOCK.md` | Time And Clock |
| `SPEC/69_DIAGNOSTICS_SUPPORT_BUNDLE.md` | Diagnostics Support Bundle |

### 70-79 — Verification and release

Testing, matrices, security/concurrency/chaos/acceptance suites, packaging, installation, release, definition of done.

| Document | Subject |
|---|---|
| `SPEC/70_TESTING_STRATEGY.md` | Testing Strategy |
| `SPEC/71_TEST_MATRIX.md` | Test Matrix |
| `SPEC/72_SECURITY_TESTS.md` | Security Tests |
| `SPEC/73_CONCURRENCY_TESTS.md` | Concurrency Tests |
| `SPEC/74_CHAOS_TESTS.md` | Chaos Tests |
| `SPEC/75_ACCEPTANCE_TESTS.md` | Acceptance Tests |
| `SPEC/76_PACKAGING.md` | Packaging |
| `SPEC/77_INSTALLATION.md` | Installation |
| `SPEC/78_RELEASE.md` | Release |
| `SPEC/79_DEFINITION_OF_DONE.md` | Definition Of Done |

### 80-89 — Implementation

Plan, agent contracts, tool contracts, execution notes.

| Document | Subject |
|---|---|
| `SPEC/80_IMPLEMENTATION_PLAN.md` | Implementation Plan |
| `SPEC/81_AGENT_CONTRACTS.md` | Agent Contracts |
| `SPEC/82_TOOL_CONTRACTS.md` | Tool Contracts |
| `SPEC/83_ANTIGRAVITY_EXECUTION.md` | Antigravity Execution |

## Generated artifacts

| Artifact | Generator | Rule |
|---|---|---|
| `SPEC/11_DATABASE_SCHEMA.md` | `generate_artifacts.build_tables_and_doc` | V31-01: `--check` diffs byte-for-byte |
| `SPEC/13_STATE_TRANSITION_MATRIX.md` | `generate_artifacts.build_state_matrix_md` | same |
| `SCHEMAS/*.schema.json` | `generate_artifacts.build_schemas` | same |
| `SCHEMAS/state-machine.json` | `generate_artifacts.build_state_machine_json` | same |
| `SCHEMAS/tables.json` | `generate_artifacts.build_tables_and_doc` | same |
| `SCHEMAS/schema.sql` | `generate_artifacts.build_canonical_ddl` | same (V31.1.1: executable DDL for load-bearing tables) |
| `BLUEPRINT/11_TRACEABILITY.md` | `generate_artifacts.build_traceability` | same |
| `MANIFEST.md`, `MANIFEST.sha256` | `TOOLS/validate_package.py --regen` | excludes itself |
