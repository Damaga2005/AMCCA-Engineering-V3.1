# AMCCA Engineering V3.1 — Autonomous Implementation Runbook for Antigravity

> **Purpose:** operational instructions for Antigravity + Claude 3.8 to implement AMCCA Engineering V3.1 from the repository specification, continuously and in controlled increments.
>
> **Normative authority:** this file does NOT override the repository specification. `DECISIONS.md`, `BUILD_ORDER.md`, the relevant `SPEC/*`, schemas, policies and locked decisions remain authoritative.

---

## 0. Mission

Implement the actual AMCCA Engineering V3.1 application from the repository specifications.

The repository currently contains the engineering specification, contracts, schemas, policies and validation tooling. There is **not yet a completed C# application implementation**. Your job is to turn the specification into production-quality code without weakening or silently changing the contracts.

You must work continuously through the implementation order, but in small verifiable increments. Never attempt to generate the entire application in one uncontrolled operation.

The goal is not "a project that compiles". The goal is an implementation that satisfies the repository's explicit contracts and demonstrates every phase exit criterion with executable tests.

---

# 1. Authority hierarchy

When deciding what to implement, use this order:

1. `DECISIONS.md` — locked architectural decisions.
2. `BUILD_ORDER.md` — mandatory phase sequence and exit criteria.
3. Relevant `SPEC/*` files for the current phase.
4. Referenced schemas in `SCHEMAS/*`.
5. Referenced policies in `POLICIES/*`.
6. `ARCHITECTURE.md`, `SYSTEM.md`, `BLUEPRINT/*`.
7. `AGENTS.md` — agent implementation contract.
8. `SPEC/80_IMPLEMENTATION_PLAN.md` — working method.
9. `CLAUDE.md` and `ANTIGRAVITY_START_PROMPT.md` — execution guidance.

Do not treat your own previous code, assumptions, generated comments or inferred conventions as higher authority than these documents.

If two normative documents conflict, **STOP** and report the conflict. Do not choose an interpretation yourself.

---

# 2. Mandatory first action: understand before coding

Before modifying code, inspect the repository and produce an implementation map.

Read at minimum:

- `README.md`
- `AGENTS.md`
- `CLAUDE.md`
- `ARCHITECTURE.md`
- `DECISIONS.md`
- `SYSTEM.md`
- `BUILD_ORDER.md`
- `ROADMAP.md`
- `SPEC/01_TECH_STACK.md`
- `SPEC/80_IMPLEMENTATION_PLAN.md`
- `SPEC/79_DEFINITION_OF_DONE.md`
- the SPEC files referenced by the current phase
- all schemas/policies referenced by those SPEC files
- relevant files under `TOOLS/`

Then report:

- current repository state;
- target technology stack;
- solution/project structure required by the specification;
- dependency graph;
- current implementation phase;
- exact first implementation task;
- required tests;
- relevant contracts/schemas;
- generated artifacts involved;
- unresolved ambiguities or conflicts.

**Do not modify files during this understanding pass.**

Once the map is produced and no blocking contradiction exists, continue automatically to the first implementation task.

---

# 3. Continuous execution model

Work in this loop:

```text
READ SPEC
  ↓
IDENTIFY CONTRACT
  ↓
WRITE FAILING TESTS
  ↓
IMPLEMENT MINIMUM CORRECT CODE
  ↓
RUN UNIT TESTS
  ↓
RUN INTEGRATION/CONTRACT TESTS
  ↓
RUN REPOSITORY VALIDATORS
  ↓
RUN FAILURE/NEGATIVE TESTS
  ↓
REVIEW DIFF
  ↓
VERIFY ACCEPTANCE CRITERION
  ↓
COMMIT
  ↓
NEXT SMALL TASK
```

Do not skip the negative-test step merely because the happy path works.

Do not proceed to the next BUILD_ORDER phase while the current phase exit criterion is not demonstrated.

---

# 4. Implementation granularity

Never use a vague instruction such as:

> "Implement Phase 5."

Instead break the phase into independently verifiable tasks, for example:

```text
001 solution and project skeleton
002 dependency injection foundation
003 configuration contract
004 startup preflight
005 logging contract
006 domain identifiers
007 domain value objects
008 state machine model
009 state transition tests
010 persistence abstraction
011 database implementation
012 migrations
013 event store
014 audit store
...
```

Each task must have a clear acceptance criterion.

A task is complete only when its tests demonstrate the criterion.

---

# 5. Code architecture — do not violate boundaries

Respect the architecture defined by the repository.

The conceptual dependency direction is:

```text
WPF / Presentation
        ↓
Application Services
        ↓
Policy Engine
        ↓
Orchestrator
        ↓
Scheduler / Workers
        ↓
Tool Registry / Provider Adapters
        ↓
Infrastructure / Storage / External Ports
```

Keep domain logic deterministic and independent from infrastructure.

External systems must be accessed through explicit ports/adapters.

Do not place network calls, model-provider calls or external side effects inside database transactions.

The orchestrator remains the authoritative state committer.

Workers perform work; they do not become the authority for state, permissions or policy.

---

# 6. Agent rules

Agents are not actors.

An agent:

- receives validated input;
- produces a structured output;
- cannot mutate protected persistent state directly;
- cannot decide that policy passed;
- cannot decide that a budget passed;
- cannot grant itself tools, autonomy, timeout or budget;
- cannot call tools absent from its contract;
- cannot expose secrets;
- cannot publish autonomously;
- must have deterministic validation around its output.

Follow `AGENTS.md` exactly.

`output_valid` must be determined by the validator, not by the agent itself.

---

# 7. External capabilities

Never invent:

- APIs;
- endpoints;
- request/response formats;
- headers;
- OAuth behaviour;
- model capabilities;
- pricing;
- rate limits;
- platform semantics.

If an external capability cannot be verified from the specification or an authoritative capability probe, implement the adapter boundary but keep the capability disabled according to the repository contract.

Never fake an integration merely to make tests pass.

---

# 8. State machine safety

Treat the state machine as a security and correctness boundary.

Every transition declared in the specification must have tests.

Every transition not declared must be rejected.

Never infer success from an unknown external state.

Use the repository's explicit unknown/reconciliation states and policies.

Terminal states must remain terminal unless the specification explicitly declares a recovery path.

---

# 9. Money and numerical correctness

Use decimal/fixed-precision representations as required by the specification.

Never use floating point for money.

Test:

- valid precision;
- invalid precision;
- negative values where prohibited;
- invalid types;
- serialization/deserialization;
- arithmetic/rounding rules.

---

# 10. Generated artifacts

Never hand-edit generated artifacts when the repository identifies a canonical generator.

Use the canonical tooling under `TOOLS/`.

After changes affecting generated contracts:

1. run the generator in the repository-prescribed mode;
2. run drift checking;
3. run conformance tests;
4. inspect the resulting diff.

If generated output conflicts with a hand-written specification, STOP and report the conflict rather than editing generated output manually.

---

# 11. Testing requirements

For every meaningful implementation unit, create tests before implementation where practical.

Tests must cover:

### Positive

- valid input;
- expected state transitions;
- expected persistence;
- expected output.

### Negative

- invalid input;
- illegal transitions;
- missing required fields;
- malformed contracts;
- forbidden operations;
- unauthorized operations;
- unavailable capabilities;
- invalid external responses.

### Failure/recovery

Where applicable:

- timeout;
- process crash;
- partial execution;
- duplicate request;
- ambiguous external response;
- retry;
- lease expiration;
- reconciliation;
- restart/resume.

Do not weaken production validation to satisfy a test. Fix the implementation or the test according to the normative contract.

---

# 12. Required validation after each task

At minimum run the relevant:

- .NET build;
- unit tests;
- integration/contract tests;
- repository validator;
- conformance tests;
- generated-artifact drift check;
- formatting/static analysis where required by the stack.

Use the exact repository commands documented in `README.md`, `SPEC/01_TECH_STACK.md` and `TOOLS/`.

If a required validator cannot run because the application infrastructure does not exist yet, state that explicitly and implement the missing foundation before claiming completion.

---

# 13. Phase execution order

Follow `BUILD_ORDER.md` exactly:

1. Repository, CI, package validator
2. Configuration, secrets, preflight
3. Database, migrations, event store, audit store
4. Domain model and state machine
5. Jobs, leases, idempotency, recovery, reconciliation
6. Tool registry and agent runtime
7. Provider gateway port + first adapter + model registry
8. Research, claims, sources, trends, opportunity scoring
9. Script, storyboard, assets, voice, render
10. Deterministic QA, AI-assisted QA, rights, duplicates
11. Rework and DAG invalidation
12. Platform hub, OAuth, capability matrix, publishing
13. Synthetic-content disclosure and compliance gate
14. Monetization, attribution, analytics, revenue
15. Memory, genome, experiments
16. Desktop UI and inspectors
17. Chaos, concurrency, security suites
18. Packaging, installer, signing, release validation

Do not reorder these phases merely because a later feature appears easier to implement.

Do not enable autonomous publishing before the repository's explicit gates have passed and the operator decision is separately made.

---

# 14. Phase gate

At the end of every phase, execute its exact exit criterion from `BUILD_ORDER.md`.

The phase is **NOT COMPLETE** unless the criterion is demonstrated.

For example, "the state machine classes exist" is not sufficient.

The acceptable result is:

> "Every transition in SPEC/13 has an executable test, every non-listed transition is rejected, and the full suite passes."

---

# 15. Git discipline

Keep commits small and meaningful.

Preferred format:

```text
feat(domain): implement state machine contract
fix(storage): make event append atomic
 test(policy): reject missing evidence
refactor(application): isolate orchestrator state commit
```

Never mix unrelated features in one commit.

Before committing:

- inspect `git diff`;
- inspect changed files;
- run tests;
- run validators;
- verify no secrets or generated junk entered the repository.

If the environment does not permit committing, continue implementation but report that the commit could not be created. Do not pretend it happened.

---

# 16. No scope creep

Do not add:

- speculative features;
- decorative UI;
- unnecessary abstractions;
- undocumented endpoints;
- hidden configuration;
- convenience bypasses;
- "temporary" security exceptions;
- silent fallback behaviour that changes semantics.

When the specification does not require something, leave it out unless it is strictly necessary to implement a specified contract.

---

# 17. Contradiction protocol

If you encounter any of these:

- two SPEC files disagree;
- a SPEC disagrees with `DECISIONS.md`;
- schema and implementation requirements disagree;
- required external capability cannot be verified;
- acceptance criterion cannot be tested;
- a necessary contract is missing;

stop the affected task and report:

```text
BLOCKED

Conflict:
Authoritative documents:
Why implementation cannot safely continue:
Possible interpretations:
What decision is required:
```

Do not silently pick the most convenient interpretation.

---

# 18. Security rules

Never put real secrets in:

- source code;
- test fixtures;
- logs;
- screenshots;
- sample configuration committed to Git;
- generated artifacts.

Use safe test values and secret placeholders.

Validate security boundaries with negative tests.

Treat authorization, policy, credentials and side effects as deterministic application responsibilities rather than AI responsibilities.

---

# 19. UI strategy

Do **not** begin by building the WPF interface.

First establish:

```text
Foundation
→ Domain
→ State machine
→ Persistence
→ Orchestrator
→ Policy
→ Scheduler
→ Workers
→ Tools
→ Provider adapters
→ Evidence
→ Agents
→ Media pipeline
→ WPF/MVVM
```

The UI should consume stable application services and inspectors rather than become the place where business rules are invented.

---

# 20. AI strategy

Do not start by making the AI "smart".

Start by making the deterministic system safe.

The correct model is:

```text
AI proposes
    ↓
Schema validation
    ↓
Deterministic policy
    ↓
Orchestrator
    ↓
Persisted state
    ↓
Controlled side effect
    ↓
Evidence
```

AI must never be the final authority for a protected state transition or quality gate.

---

# 21. Mandatory phase report

At the end of every phase produce this report:

```text
AMCCA V3.1 — PHASE REPORT

Phase:
Status: COMPLETE / NOT COMPLETE / BLOCKED

Files created:
- ...

Files modified:
- ...

Contracts added/changed:
- ...

Schema/policy changes:
- ...

Migrations:
- ...

Tests added:
- ...

Tests executed:
- total:
- passed:
- failed:

Validators:
- validate_package:
- conformance:
- generated-artifact drift:
- build:
- other:

Failure/recovery coverage:
- ...

Known limitations:
- ...

Exit criterion:
- exact criterion from BUILD_ORDER.md

Evidence proving criterion:
- test/command/result

FINAL VERDICT:
COMPLETE only if the exit criterion is demonstrated.
Otherwise: NOT COMPLETE.
```

---

# 22. Definition of done

Use `SPEC/79_DEFINITION_OF_DONE.md` as the final authority.

Never declare success because:

- the code compiles;
- the UI opens;
- a demo works;
- the happy path works;
- an AI response looks correct.

Completion requires executable evidence for the applicable specification criteria.

---

# 23. First execution prompt

Use the following as the first instruction when starting the implementation session:

> Read the AMCCA Engineering V3.1 repository completely enough to establish the authoritative implementation map. Start with `DECISIONS.md`, `BUILD_ORDER.md`, `SPEC/80_IMPLEMENTATION_PLAN.md`, `SPEC/79_DEFINITION_OF_DONE.md`, `ARCHITECTURE.md`, `SYSTEM.md`, `AGENTS.md`, `CLAUDE.md`, `ANTIGRAVITY_START_PROMPT.md`, `SPEC/01_TECH_STACK.md`, and all SPEC/schema/policy files required by Phase 1. Do not modify anything during this first pass. Report the current implementation state, target solution structure, dependency graph, exact Phase 1 tasks, required tests, validators, generated artifacts, and any contradictions. If there is no blocking contradiction, immediately begin Task 001 using the workflow in `ANTIGRAVITY_AUTONOMOUS_IMPLEMENTATION.md`: tests first, minimal implementation, validation, acceptance criterion, then commit and continue to the next task. Do not jump ahead to UI or AI features. Do not invent missing requirements. If blocked, stop only the affected task and report the exact conflict.

---

# 24. Subsequent execution rule

After a successful task, automatically continue with the next smallest task in the current phase.

After a successful phase gate, automatically continue to the first task of the next phase.

Stop only when:

1. a normative contradiction exists;
2. required information/capability cannot be verified;
3. a human decision explicitly required by a locked decision is needed;
4. the environment prevents safe execution;
5. a test exposes an unresolved defect that cannot be safely fixed from the specification.

Do **not** stop merely because a task is substantial. Break it down further.

---

# 25. Critical principle

**Never optimize for apparent progress. Optimize for verified progress.**

One small task with tests and a demonstrated contract is better than one hundred generated files that merely compile.

The implementation must converge toward the repository specification, not toward whatever implementation is easiest for the model to generate.
