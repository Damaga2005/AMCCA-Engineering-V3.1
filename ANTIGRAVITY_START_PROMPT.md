# AMCCA V3 — Master Implementation Prompt

You are implementing AMCCA from this repository. This is an engineering task, not a brainstorming task.

## Read first, in this order

1. `DECISIONS.md`
2. `BLUEPRINT/10_OPERATIONAL_INVARIANTS.md`
3. `BLUEPRINT/00_MASTER_BLUEPRINT.md`
4. `README.md` and `CLAUDE.md`
5. `AGENTS.md`
6. The SPEC files for the phase you are on, found via `BLUEPRINT/11_TRACEABILITY.md`

There is exactly one package and exactly one Blueprint. If you have been handed an "AMCCA V2" or
"Blueprint V2.1" archive, discard it: both are withdrawn and both contain known defects
(see `AUDIT/V2_DEFECTS_CLOSED.md`).

## Absolute rules

1. Do not invent external APIs, routes, headers, capabilities or pricing. When a capability is uncertain,
   implement the adapter boundary and leave it disabled with `capabilities_verified=false`.
2. Deterministic code controls state, budgets, permissions, credentials, files, hashes, retries and side effects.
3. Agents return structured proposals and results. They never mutate protected state and never decide that a gate passed.
4. Never convert unknown external state into success or failure without reconciliation evidence.
5. Never expose secrets in source, logs, tests, screenshots or exports.
6. Do not enable autonomous publishing during development.
7. Money is decimal. Never float.
8. Do not hand-edit generated files. Run `python TOOLS/validate_package.py --regen`.
9. Every phase compiles, has tests, and passes the package validator.
10. **If a requirement conflicts with a locked decision, or two documents disagree, stop and surface the
    conflict.** Do not resolve it yourself. This rule exists because V2 was released with four such
    conflicts unresolved, and each one became a defect.

## Execution order

Follow `BUILD_ORDER.md` exactly. Do not start a phase whose predecessor has failing tests.

## Reporting at the end of each phase

Report: files changed; contracts added or changed with their version bumps; migrations applied;
tests executed with pass/fail counts; `validate_package.py` result; known limitations; and an explicit
statement of whether the phase exit criterion in `BUILD_ORDER.md` is met.

Do not report a phase as complete on the basis of code existing. Report it complete on the basis of its
exit criterion being demonstrated by a test.

## What "done" is not

Not: it compiles. Not: the happy path works. Not: the demo published a video.
Done is defined in `SPEC/79_DEFINITION_OF_DONE.md` and every criterion there is checkable by execution.
