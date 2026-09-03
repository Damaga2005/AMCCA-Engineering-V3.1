# AMCCA V3.1 — Antigravity Continuous Execution Loop

## Purpose

This document is the operational loop for Antigravity + Claude 3.8 after the repository has been understood.

It is subordinate to `DECISIONS.md`, `BUILD_ORDER.md`, `SPEC/*`, schemas and policies. It exists to prevent premature stopping, uncontrolled bulk generation, false completion and silent specification drift.

---

# 1. Startup

Before changing code:

1. Read `ANTIGRAVITY_AUTONOMOUS_IMPLEMENTATION.md`.
2. Read `ANTIGRAVITY_START_PROMPT.md`.
3. Read `AGENTS.md`.
4. Read `DECISIONS.md`.
5. Read `BUILD_ORDER.md`.
6. Read `SPEC/80_IMPLEMENTATION_PLAN.md` and `SPEC/79_DEFINITION_OF_DONE.md`.
7. Read the SPEC/schema/policy files required by the current phase.
8. Inspect the actual repository tree and existing implementation.
9. Determine the smallest unfinished task.

Do not assume that the last task is complete merely because code exists. Verify its acceptance criterion.

---

# 2. Task loop

For every task execute exactly this cycle:

```text
TASK SELECTED
    ↓
SPEC CHECK
    ↓
CONTRACT IDENTIFIED
    ↓
ACCEPTANCE CRITERION WRITTEN
    ↓
NEGATIVE TESTS WRITTEN
    ↓
POSITIVE TESTS WRITTEN
    ↓
IMPLEMENTATION
    ↓
BUILD
    ↓
UNIT TESTS
    ↓
INTEGRATION / CONTRACT TESTS
    ↓
FAILURE / RECOVERY TESTS
    ↓
REPOSITORY VALIDATORS
    ↓
GENERATED ARTIFACT DRIFT CHECK
    ↓
SECURITY / SECRET SCAN
    ↓
DIFF REVIEW
    ↓
ACCEPTANCE CRITERION VERIFIED
    ↓
COMMIT
    ↓
TASK RETROSPECTIVE
    ↓
NEXT TASK
```

If any required stage fails, repair the defect and repeat the loop.

Do not proceed merely because the failure appears unrelated. Determine whether it is a regression first.

---

# 3. Automatic self-review

Before declaring any task complete, ask yourself:

### Specification

- Did I implement exactly what the SPEC requires?
- Did I accidentally add unspecified behaviour?
- Did I reinterpret an ambiguous requirement?
- Did I modify a normative document to make implementation easier?

### Architecture

- Did dependency direction remain valid?
- Did domain logic acquire infrastructure dependencies?
- Did an adapter leak into domain/application code?
- Did a worker become an authority?
- Did the orchestrator remain the state committer?

### Security

- Can an untrusted input bypass validation?
- Can an agent perform an operation outside its contract?
- Can an external unknown state become success/failure without evidence?
- Did any secret enter source, logs, tests or artifacts?
- Did I add a bypass for convenience?

### Reliability

- What happens if the process dies here?
- What happens if the external operation times out?
- What happens if the response is ambiguous?
- What happens if the same operation is delivered twice?
- What happens after restart?

### Testing

- Is there at least one test proving the happy path?
- Is there a test proving the invalid path is rejected?
- Is there a test for the important failure mode?
- Does the test prove the actual contract rather than implementation details?

### Completion

- Is the acceptance criterion demonstrated by execution?
- Did all required validators pass?
- Is the working tree clean except for intended changes?

If any answer is uncertain, the task is **NOT COMPLETE**.

---

# 4. Automatic defect repair

When a test fails:

1. Reproduce it.
2. Determine whether the test, implementation or specification is authoritative.
3. Inspect the relevant contract.
4. Fix the smallest correct layer.
5. Add/regress a test if the defect was previously uncovered.
6. Re-run the focused test.
7. Re-run the affected suite.
8. Re-run the full required validation.

Never solve a production defect by weakening the test or removing the requirement.

Never change a SPEC merely because the implementation is inconvenient.

---

# 5. Automatic continuation

When a task passes:

1. Record the result.
2. Commit the task.
3. Re-read the current phase exit criterion.
4. Identify the next smallest unfinished task.
5. Continue automatically.

When all tasks in a phase appear complete:

1. Re-run the entire phase suite.
2. Run the exact phase exit criterion from `BUILD_ORDER.md`.
3. Verify the result is reproducible from a clean checkout/build where practical.
4. Produce the mandatory phase report.
5. Only then enter the next phase.

Never skip the phase gate.

---

# 6. Block handling

Stop only the affected work when genuinely blocked.

A blocking condition includes:

- normative documents conflict;
- required information is missing;
- an external capability cannot be verified;
- required schema/contract does not exist;
- acceptance criterion cannot be determined safely;
- environment prevents safe execution.

Use:

```text
BLOCKED

Task:
Phase:

Conflict / missing capability:

Authoritative sources:

Why implementation cannot safely continue:

What has already been implemented safely:

Exact human decision or missing evidence required:
```

Do not stop the entire project if another independent task can safely proceed, unless the build order makes the blocked task a prerequisite.

---

# 7. Context-window protection

If the context becomes large:

1. Do not start rewriting architecture from memory.
2. Persist a concise task state in the repository if appropriate.
3. Record completed tasks and their test evidence.
4. Record the current task and exact acceptance criterion.
5. Record unresolved blockers.
6. Re-read authoritative files after context compaction.
7. Continue from the recorded state.

The repository is the source of truth, not conversational memory.

---

# 8. Scope control

At every task ask:

> Is this change required by the current contract or strictly necessary to implement it?

If not, defer it.

Maintain a small `TODO` only for discovered work that is outside the current task. Do not silently expand scope.

---

# 9. Commit gate

A commit is allowed only after:

- focused tests pass;
- affected tests pass;
- required full suite passes;
- validators pass;
- no secrets are present;
- no unintended generated files are present;
- diff has been reviewed;
- acceptance criterion is demonstrated.

Commit message examples:

```text
feat(foundation): create solution and test projects
feat(domain): implement state transition contract
test(domain): cover illegal state transitions
feat(storage): add append-only event store
fix(recovery): reconcile ambiguous external outcome
```

---

# 10. Phase gate matrix

Use the exact exit criteria from `BUILD_ORDER.md`.

| Phase | Gate must demonstrate |
|---|---|
| 1 | Repository validator is green in CI on every commit |
| 2 | Invalid configuration and literal secrets abort startup |
| 3 | Upgrade, rollback and restore tests pass |
| 4 | Every declared transition passes; every undeclared transition is rejected |
| 5 | Crash/recovery/idempotency/reconciliation behaviour survives failure tests |
| 6 | Forbidden agent tools are blocked and audited |
| 7 | Provider/model cannot be enabled without capability verification |
| 8 | Insufficiently sourced material claims cannot become VERIFIED |
| 9 | Full artifact DAG is complete and acyclic |
| 10 | AI findings alone cannot produce a final PASS |
| 11 | QA failure produces bounded targeted rework |
| 12 | Chaos testing proves no duplicate publication |
| 13 | Required synthetic-content disclosure blocks publication when absent |
| 14 | Estimates cannot enter the revenue ledger |
| 15 | PROVEN requires measured data |
| 16 | Every required gate/block is visible and explainable in UI |
| 17 | Chaos, concurrency and security suites pass |
| 18 | Clean install/upgrade/uninstall-preserve/restore are verified |

The table is a quick reference only. `BUILD_ORDER.md` remains authoritative.

---

# 11. Never-do list

Never:

- generate the entire product in one pass;
- start with WPF before stable application contracts;
- start with AI before deterministic foundations;
- fake external APIs;
- bypass policy to make a demo work;
- use AI output as authority;
- convert UNKNOWN into success/failure without evidence;
- use floats for money;
- commit secrets;
- hand-edit generated artifacts when a generator is authoritative;
- mark a phase complete without its exit criterion;
- hide a failing test;
- delete a test because it exposes a real defect;
- silently resolve a document contradiction.

---

# 12. Final project loop

After Phase 18:

```text
FULL CLEAN BUILD
    ↓
FULL TEST SUITE
    ↓
CHAOS / CONCURRENCY / SECURITY
    ↓
PACKAGE VALIDATION
    ↓
INSTALL TEST
    ↓
UPGRADE TEST
    ↓
UNINSTALL-PRESERVE TEST
    ↓
RESTORE TEST
    ↓
RELEASE EVIDENCE REVIEW
    ↓
SPEC/79 DEFINITION OF DONE
    ↓
FINAL RELEASE DECISION
```

Even after Phase 18, autonomous publishing remains disabled until the repository's separately required operator decision is explicitly made and audited.

---

# 13. Canonical instruction to resume after interruption

Use this instruction whenever the Antigravity session is restarted:

> Resume AMCCA Engineering V3.1 from the repository, not from conversational memory. Read `ANTIGRAVITY_AUTONOMOUS_IMPLEMENTATION.md`, `ANTIGRAVITY_LOOP.md`, `DECISIONS.md`, `BUILD_ORDER.md`, `AGENTS.md`, and the SPEC/schema/policy files for the current phase. Inspect the working tree and test state. Determine the last task whose acceptance criterion is actually demonstrated. Do not assume unfinished work is complete. Continue with the smallest unfinished task. Follow the full loop: contract → tests → implementation → build → tests → failure/recovery → validators → drift/security review → acceptance criterion → commit → next task. Continue automatically until blocked by a genuine specification conflict, missing required evidence/capability, required human decision, or unsafe environment condition.

---

# 14. Principle

**The loop continues because evidence says it can, not because the model feels confident.**

A green test is evidence. A compilation is only a prerequisite.

A completed phase is evidence of its exit criterion. A directory full of code is not.
