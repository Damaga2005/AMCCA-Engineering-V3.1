# Architecture Decision Record — Locked Decisions

> **Purpose:** prevent implementation agents and future contributors from making inconsistent
> architectural choices. This is the highest authority in the package (see `README.md`, §Source of truth).

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

> **Amendment rule:** a decision is changed only by editing this file and bumping `PACKAGE_VERSION`.
> An implementation that finds a conflict between a decision and any other document MUST stop and
> surface the conflict rather than choose. Choosing silently is the failure mode this file exists to prevent.

---

## Foundation

### D-001 Product boundary
AMCCA is a Windows desktop application with a local durable execution core. It is not a SaaS backend in V3.
Remote services are external dependencies reached through adapters. No mandatory remote AMCCA backend exists.

### D-002 Desktop stack
**C# / .NET 8 LTS + WPF**, MVVM. WebView2 only where a provider requires a web-based OAuth or console.
Electron, Tauri and Python are not runtime foundations. Python MAY be used for build-time tooling only.

### D-003 Persistence and identifiers
SQLite with WAL, `foreign_keys=ON`, configured `busy_timeout`, versioned migrations,
Dapper + `Microsoft.Data.Sqlite`. EF Core is not used in the core.
All primary identifiers are **ULID** strings generated locally. An external identifier is never a primary key,
because an external system is entitled to change or reuse it and the local database is not entitled to break.

### D-004 Contracts and validation
System.Text.Json plus JSON Schema (draft 2020-12) validation at every trust boundary:
agent output, tool output, provider response, import, and configuration at startup.
**Every persisted contract object carries `schema_version`.** Every schema in `SCHEMAS/` sets
`additionalProperties: false`; an unexpected field is a contract violation, not a curiosity.

### D-005 HTTP
Typed clients through `Microsoft.Extensions.Http` / `HttpClientFactory`. Timeouts, retries and
circuit breakers are policy-driven configuration, never per-call literals.

### D-006 Resilience
Polly for bounded retry, timeout, circuit breaker and rate limiting.
**An unknown external side effect is never retried blindly** — see D-016.

### D-007 Logging
Serilog structured logging with a redaction middleware that runs before any sink.
Secrets, authorization headers, cookies, tokens and raw provider payloads are not logged by default.
Redaction is verified by a security test, not by convention (SPEC/72).

### D-008 Media
FFmpeg as an external process behind a single hardened `MediaWorker`.
Arguments are passed as an argument list via `ProcessStartInfo`; string concatenation into a shell is forbidden.
Every FFmpeg invocation has a timeout, an output size ceiling and a working directory confined to the artifact store.

### D-009 Secrets
Windows DPAPI / Credential Manager behind a `SecretStore` abstraction.
The database and configuration hold only opaque `secret://` references.
A literal credential found in configuration is a **startup failure**, not a warning.

### D-010 Packaging
Self-contained .NET publish for x64; WiX Toolset installer. Release artifacts are versioned and SHA-256 hashed.
Code signing is required for a production release.

### D-011 Local process model
UI and execution core share one application host. Long-running work runs in managed background workers;
FFmpeg runs in isolated child processes. A localhost HTTP API is **not** required for core operation.

### D-012 External side effects
Publishing, affiliate changes, credential refresh and every other external mutation happen only through
typed adapters and are **persisted as an intent, in a committed transaction, before the call is made**.

---

## Intelligence boundary

### D-013 Provider gateway is an abstraction, not a vendor
The AI gateway is accessed through an `IProviderGateway` port. A concrete gateway (currently OmniRouters,
`SPEC/23`) is one adapter behind that port. Nothing above the adapter layer may reference a gateway-specific
type, header or route.

*Rationale, stated plainly:* the entire multimodal capability of this system rests on one external service whose
long-term availability nobody controls. The abstraction is not architectural decoration; it is the only thing that
makes a gateway migration a two-week job instead of a rewrite. **A second adapter implementation, even a
minimal one, MUST exist before autonomous mode is enabled**, because a port with exactly one implementation
has never actually been tested as a port.

### D-014 Current information comes from research, never from a model
Current or trending facts come from timestamped research sources with a `retrieved_at`.
A language model is never treated as a database of current events. This is not a preference; a model's
training cutoff makes it structurally incapable of being authoritative about the present.

### D-015 Agent authority
Agents reason and return typed proposals and results. They MUST NOT mutate protected state, perform
external side effects directly, or assert that a policy, budget, rights or QA check has passed.
An agent may only supply evidence to the deterministic engine that decides.
Autonomy is a policy-controlled permission set; **an agent cannot elevate its own autonomy, budget or permissions.**

### D-016 Unknown external state
Any external operation whose outcome is not definitively known enters `UNKNOWN_EXTERNAL_STATE` and
requires reconciliation before any retry. Unknown is never silently converted to success or to failure.
This is the single most important behavioural rule in the system.

### D-017 Binary storage
SQLite stores metadata, not media. Artifacts live under the AMCCA data directory and are referenced by
content hash and relative path.

### D-018 Event integrity *(amended in V3)*
Domain events are append-only and physically separate from audit records.
**Every event row carries `event_id`, `aggregate_type`, `aggregate_id`, `aggregate_version`,
`correlation_id`, `causation_id`, `schema_version` and `occurred_at`**, and a production state-change event
additionally carries the `transition_id` from `SPEC/13`.

> *V2 defect this amendment closes:* D-018 required correlation and causation identifiers, but
> `event.schema.json` neither declared them nor permitted them (`additionalProperties: false`).
> The decision and the contract now agree, and `TOOLS/validate_package.py` fails the build if they diverge again.

### D-019 Versioning and reproducibility
Prompts, agent contracts, policies, model configuration and artifacts are independently versioned and
linked to the productions that used them. An agent run is reproducible from
`agent_version + prompt_version_id + model_id + model_params_hash + input_hash`.

### D-020 Safe defaults
A fresh installation starts with publishing disabled, `MANUAL` autonomy, `dry_run` enabled,
no credentials configured and no models enabled. Every capability is off until explicitly turned on
by an operator with evidence that it works.

---

## Decisions added in V3

Each of these exists because the V2 audit found a concrete defect. The defect is named so nobody
re-opens the question without knowing what it cost.

### D-021 The Blueprint is normative and ranked
`BLUEPRINT/` is part of the normative package and sits **below** `DECISIONS.md` and above SPEC files
for questions of *boundary and authority*; SPEC files win on questions of *detail*. Specifically:
`BLUEPRINT/10_OPERATIONAL_INVARIANTS.md` is normative and its invariants override any SPEC text that
contradicts them.

> *V2 defect:* the source-of-truth order omitted `BLUEPRINT/` entirely, while
> `BLUEPRINT/04_OPERATIONAL_INVARIANTS.md` contained fifteen hard invariants. Their authority was undefined.

### D-022 One package, one document per subject
There is exactly one package. There are no duplicate-numbered or duplicate-subject SPEC files.
A subject has one home; other documents link to it rather than restating it.

> *V2 defect:* two divergent Blueprints shipped in two archives, and the hashed package contained the
> superseded one. Six pairs of SPEC files overlapped, including two different Definitions of Done.

### D-023 Money is decimal, never floating point
Every monetary value is stored and transported as a decimal string with six fractional digits and an
explicit ISO-4217 currency. `REAL`, `double` and `float` are forbidden for money in storage, contracts and
arithmetic. Budget arithmetic uses `decimal` in C#.

### D-024 QA verdicts are deterministic
A QA `verdict` is computed by deterministic code from scores and findings. An AI-assisted check produces
**evidence only** (`check_kind: AI_ASSISTED`) and can never, alone, produce a `PASS`.
A production cannot reach `FINAL_VERIFIED` on AI self-certification.

### D-025 Generated artifacts and drift detection *(amended by V3.1)*
`SPEC/11_DATABASE_SCHEMA.md`, `SPEC/13_STATE_TRANSITION_MATRIX.md`, `SCHEMAS/state-machine.json`,
`SCHEMAS/*.schema.json`, `SCHEMAS/tables.json`, `BLUEPRINT/11_TRACEABILITY.md`, `MANIFEST.md` and
`MANIFEST.sha256` are **generated**. The canonical models in `TOOLS/generate_artifacts.py` are the only
editable source for them. Generated artifacts MUST NOT be hand-edited.

`TOOLS/generate_artifacts.py` MUST deterministically regenerate every derived artifact, in memory, from
the canonical models. `TOOLS/validate_package.py` MUST compare that fresh generation byte-for-byte against
the checked-in files. Any difference is a release failure — `AMCCA-DB-002`-style checksum comparison is
not sufficient on its own; a hand-edit that happens to keep an old marker string intact must still fail.

`--regen` MAY update generated artifacts on disk. `--check` (the default mode of `validate_package.py`, and
the explicit mode of `generate_artifacts.py --check`) is the mandatory release gate and MUST NOT be
skipped.

> *V2 defect, and the V3 defect this amendment closes (V31-01):* V3's original `--regen` wrote generated
> artifacts but nothing compared the result to what was checked in beyond a "does this file contain the
> words 'Generated artifact'" marker check. A hand-edit of a generated file that left the marker comment
> untouched would still pass. The byte-for-byte `--check` comparison closes that gap structurally.

### D-026 Synthetic-content disclosure is a blocking publication gate *(amended by V3.1)*
Content generated or materially altered by AI carries a synthetic-content declaration, and any
platform-native AI-content label required by the target platform MUST be applied before the publication
intent is dispatched. A missing required label blocks publication exactly as a missing rights clearance does.

This gate is **structural**, not merely procedural: `publications.synthetic_declaration_id` links each
publication to its declaration, and `state = VERIFIED` is unreachable — both in the JSON Schema and in the
database `CHECK` — while `platform_label_required = true` and `synthetic_label_applied` is not `true`.
This holds even if the preflight code path that is supposed to enforce it has a bug.

Responsibility for the obligations touching AI-generated content is **not uniform**: provider,
deployer, platform mechanism and AMCCA's own internal control each owe different things. The matrix in
`SPEC/45` assigns each obligation to its owner. AMCCA's hard, non-negotiable gate is the deployer
disclosure; C2PA provenance propagation is a SHOULD, tracked separately, and is never treated as
satisfying the disclosure requirement.

> *V2 defect:* the words "AI-generated", "synthetic", "watermark" and "label" appeared **zero times**
> across 133 files, in a system whose purpose is publishing AI-generated video.
> *V3 defect this amendment closes (V31-07, V31-08):* the V3 gate depended on every code path correctly
> reaching the preflight check, and did not distinguish which party — provider, deployer or platform —
> owed which specific obligation, risking either over-blocking content whose provenance metadata a
> provider didn't supply, or treating C2PA presence as if it satisfied the disclosure duty.
> **Verification obligation:** the specific labelling duties per platform and jurisdiction are runtime facts
> and MUST be re-verified against primary sources before production use. See `SPEC/45`.

### D-027 Personal data is a tracked class
Research claims and sources are flagged when they contain personal data. Personal data is minimised,
retained on a shorter clock than operational data, excluded from exports by default, and never used to
train or fine-tune anything. See `SPEC/51`.

### D-028 Fail closed on unverified capability
Any capability whose verification is absent, stale or failed is **disabled**, not assumed. This applies to
models, platform capabilities, referral programs and provider features alike. The absence of evidence is
evidence of absence for the purpose of granting permission.

### D-029 No self-certifying audits
A release gate is satisfied by an executable check whose output is a machine-readable result, never by a
prose document asserting that a gate passed.

> *V2 defect:* `AUDIT/FINAL_AUDIT.md` declared eighteen dimensions `RESOLVED` and the package
> `implementation-ready`. Four of those dimensions contained the worst defects in the package, and its
> own word count was not reproducible. A document cannot audit itself.

### D-030 Estimates and measurements are different types
Expected revenue, estimated cost and projected performance are structurally distinct from confirmed
revenue, settled cost and measured performance. They live in different tables with different provenance
constraints. An estimate is not permitted to enter the revenue ledger — the database `CHECK` constraint,
not a code review, is what enforces this.

## Decisions added in V3.1

Each of these closes a defect found in the V3.1 audit. As with V3's own decisions, the defect is named
so nobody re-opens the question without knowing what it cost.

### D-031 Money has two conceptual types: NonNegativeMoney and SignedMoney
Not every monetary field has the same semantics. A budget limit, an estimate, a reservation or a
settlement cannot be negative; an accounting adjustment (a provider refund, an under-billed correction)
or a revenue reversal legitimately can be. `NonNegativeMoney` (`^[0-9]{1,13}\.[0-9]{6}$`) is the default
for every monetary field. `SignedMoney` (`^-?[0-9]{1,13}\.[0-9]{6}$`) is used only where the field's
`kind` or `state` can legitimately be a signed correction: `cost_events.amount` when `kind = ADJUSTMENT`,
and `revenue_events.amount` when `state = REVERSED`. Both the JSON Schema conditionals and the database
`CHECK` constraints enforce the split; a negative budget or a negative settlement is refused at both layers.

> *V3 defect closed (V31-04):* the original money pattern admitted a leading `-` unconditionally, so
> nothing prevented a negative daily budget from validating.

### D-032 Format constraints are verified to actually run, not merely declared
Declaring `format: "date-time"` in a JSON Schema does not, by itself, cause `jsonschema` to reject a
malformed timestamp — `FormatChecker()` requires the `rfc3339-validator` package to be installed, and
silently accepts everything if it is absent, with no error and no warning. Every schema construction site
in this package's tooling MUST pass `format_checker=FormatChecker()`, `rfc3339-validator` MUST be a pinned
dependency (`TOOLS/requirements.txt`), and `TOOLS/validate_package.py` MUST assert that the `date-time`
format checker is actually registered before trusting any other format-dependent result.

> *V3 defect closed (V31-02):* V3's schemas declared `format: "date-time"` throughout and the validator
> constructed `Draft202012Validator` instances without `format_checker`, so the declared constraint was
> pure documentation. This was confirmed empirically while building V3.1: the negative date-time test
> cases failed until `rfc3339-validator` was installed and wired in.

### D-033 A discovered capability is not a verified one
`platform_capabilities.status` distinguishes `DISCOVERED` (found via a secondary source — a blog, an
agency article, community documentation) from `VERIFIED` (confirmed via `OFFICIAL_API`,
`OFFICIAL_DASHBOARD`, `OFFICIAL_DOCUMENTATION`, `DIRECT_PLATFORM_PROBE` or `OPERATOR_CONFIRMATION`).
A database `CHECK` constraint makes it impossible for a `VERIFIED` row to carry a secondary-source
`evidence_source`, regardless of what application code intended. The same distinction applies to
publication evidence (D-026 amendment, `publications.evidence_source`): `POST_PUBLISH_CHECK` — confirming
only that a URL resolves — can support intermediate states but can never, by itself, satisfy `VERIFIED`.

> *V3 defect closed (V31-06, V31-09):* the prior evidence-source value naming a plain resolving-URL check
> was accepted inside the conditional that gates `VERIFIED`, so proof only that a URL exists — not that
> the content is genuinely published — could satisfy the invariant the schema was written to enforce. The
> value was renamed and the authoritative-evidence enum tightened to exclude it from `VERIFIED` entirely.

## Decisions added in the fifth audit remediation

Each closes a finding from `AUDIT/FIFTH_AUDIT_CODE.md`. Added the same way D-031..D-033 were:
by editing this file. They introduce no new external service or framework.

### D-034 Model token prices come only from configuration, and a missing price is not zero
`config.providers.gateway.model_pricing` is the sole source AMCCA prices a model call against. Provider
pricing is external and volatile (SPEC/21), so each entry carries its own `retrieved_at` and
`source_ref`, and the entries are materialised — once, idempotently — into `pricing_snapshots` rows
(`PricingSnapshotModelPricing`); this is the ingestion pipeline migration 009 disclosed as missing.
`AgentRuntime.RunAgentAsync` reads the gateway's reported `InputTokens`/`OutputTokens` on every turn,
prices them with `ModelCostCalculator` (`decimal` only, per-1M-token rate, rounded up to six fractional
digits), folds the amount into `AgentRunSession.AccumulatedCost` so the existing `MaxCost` gate enforces
model spend, and settles one `cost_events` row of kind `SETTLEMENT` per run. When no snapshot resolves
for a model, the run still completes but its cost event is `reconciliation_state = ESTIMATED_UNRECONCILED`
with whatever could be priced — a known unknown on the books (SPEC/21), never a silent zero, and never
an invented price.

> *Fifth-audit defect closed (H1):* `AgentRuntime` discarded `GatewayTextResponse.InputTokens` and
> `OutputTokens` entirely. `RevenueService.RecordCostAsync` was called from nowhere, `contract.MaxCost`
> was a dead gate for model usage, and no `cost_events` row was ever written for an agent run. Model
> spend was invisible to the budget engine and to profit.

### D-035 CONCEPT_SELECTED is a decision gate, not a bookkeeping state
`CONCEPT_SELECTED` (SPEC/12 kind=gate, SPEC/13 T-003/T-004, SPEC/29) is handled by
`ConceptSelectionStageHandler`, never by `NoWorkAdvanceHandler`. The gate: takes the production's
operator-selected opportunity, or in AUTONOMOUS mode the eligible `SCORED` opportunity with the highest
**pre-computed** score (the score is never re-derived here); commits the scripting budget reservation
against the `PRODUCTION` budget; links the opportunity to the production and flips it to `SELECTED`,
which is itself the persisted strategy decision and its immutable expected-value snapshot; and writes a
`CONCEPT_SELECTED` audit row. If no opportunity is selectable, or the budget reservation is refused, the
gate returns `BLOCKED` with a SPEC/05 reason code for an operator. It never advances silently. The
orchestrator still commits the state transition (DEF-008).

> *Fifth-audit defect closed (M4):* `CONCEPT_SELECTED`, a `kind: gate` state, was wired to
> `NoWorkAdvanceHandler`, which auto-advances with no logic in AUTONOMOUS mode. The gate's T-003/T-004
> exit criteria — strategy decision persisted with an expected-value snapshot, scripting budget
> reservation committed — were simply not implemented; the state was transited as a no-op, a silent
> bypass of a decision gate. This resolves the contradiction between SPEC/13 and the former handler's
> own doc comment in favour of SPEC/13.
