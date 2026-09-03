# AMCCA — Master Blueprint V3

## 1. Purpose

AMCCA (Autonomous Multimodal Content Creation & Monetization Center) is a Windows-first, local-first
autonomous business system. Its purpose is not to generate content. It is to close the loop from
opportunity discovery through production, publication, measurement, attribution and revenue
reconciliation back into strategy — with every cycle independently traceable.

Content generation is the easy part of that sentence and the least important. The difficulty, and the
reason this package is long, is everything that keeps an autonomous loop from doing damage while
nobody is watching.

## 2. Core business loop

`Signals -> Trends -> Niches -> Opportunities -> Strategy -> Research -> Script -> Storyboard ->
Assets -> Audio -> Edit -> QA -> Compliance -> Publication -> Analytics -> Attribution -> Revenue ->
Learning -> Strategy`

The loop is continuous. Every cycle is traceable through immutable artifact versions, append-only events,
recorded costs and retained evidence.

## 3. Architectural law

AMCCA separates **probabilistic intelligence** from **deterministic control**.

AI and agents may propose: research interpretations, opportunities, hooks, scripts, storyboards, asset
plans, rework plans, and quality *evidence*.

Deterministic application services remain authoritative for:

- schemas and contracts
- state transitions
- permissions and autonomy
- policy gates and their decisions
- budget reservation and settlement
- credentials and secrets
- files, hashes and artifact lineage
- retries and idempotency
- external side-effect intents
- publication verification
- synthetic-content and rights compliance
- measured analytics and revenue provenance

The dividing line is not "AI is unreliable". It is that **authority requires accountability**, and a
probabilistic component cannot be held accountable for a decision it cannot reproduce.

## 4. System boundary

**Inside AMCCA:** Desktop Control Center; application and domain services; scheduler; worker pool;
orchestrator; policy engine; agent runtime; tool registry; provider gateway port; research and
opportunity engines; content and media pipeline; QA; rights; duplicate detection; compliance and
disclosure engine; publication hub; monetization; analytics; memory, genome and experiments;
cost, storage, security and policy subsystems; event and audit stores; backup, import and export;
release tooling; the package validator.

**Outside AMCCA:** official platform APIs; affiliate and merchant systems; external research sources;
the AI provider gateway and its upstream model providers; operating-system facilities; user accounts and
credentials; internet connectivity; external media services.

AMCCA never treats an external system as authoritative merely because a request returned HTTP 200.
Capability verification and domain-specific evidence are required, separately.

## 5. Layers

1. **Presentation** — WPF/MVVM Control Center.
2. **Application control** — typed commands, orchestration, policy, approvals, scheduling.
3. **Domain intelligence** — opportunity, research, strategy, content and monetization engines.
4. **Execution** — durable jobs, leases, workers, tools, media processing, provider clients.
5. **Persistence** — SQLite metadata, append-only event history, separate audit history, versioned records.
6. **Artifact storage** — hash-addressed filesystem media with manifests and retention rules.
7. **External boundary** — ports only: gateway, platform adapters, research sources, monetization providers, secret store.

## 6. Business truth

- A forecast is never revenue.
- An estimated metric never overwrites a measured metric.
- Revenue is financially meaningful only after confirmation with recorded provenance.
- Profit is confirmed revenue minus attributable settled costs. Not reserved costs. Not estimates.
- A niche becomes `PROVEN` only through measured performance.
- Affiliate validity is never inferred from a successful HTTP request.
- A publication is `VERIFIED` only from authoritative platform evidence, never from our own optimism.

These six lines are enforced by database constraints, not by discipline. See `SPEC/11`.

## 7. Safety and autonomy boundary

Fresh installations default to `MANUAL`, `publishing_enabled=false`, `dry_run=true`, no credentials,
no enabled models and no autonomous publishing.

An agent can never grant itself higher autonomy, budget, permissions or publishing authority.
The kill switch is persisted in the database; `EMERGENCY_STOP` survives restart and cannot be cleared
by any non-operator actor.

## 8. Runtime topology

`AMCCA.exe` — WPF shell and UI thread; application services; orchestrator; policy engine; scheduler;
worker supervisor; agent runtime; provider clients; platform adapters; SQLite connection layer;
artifact and storage manager; reconciliation service; observability, event and audit pipeline.
FFmpeg runs as isolated child processes.

No mandatory remote AMCCA backend exists in V3.

## 9. Definitive principle

The Blueprint describes stable composition and control boundaries. Detailed types, fields, endpoints,
schemas, algorithms, test cases and provider-specific capabilities live in SPEC and SCHEMAS and
**must not be invented by implementation agents.**

Where the Blueprint and a SPEC file disagree on a *boundary or authority* question, the Blueprint wins.
Where they disagree on a *detail*, the SPEC wins. Where the disagreement is material, stop and escalate
to `DECISIONS.md` (D-021).
