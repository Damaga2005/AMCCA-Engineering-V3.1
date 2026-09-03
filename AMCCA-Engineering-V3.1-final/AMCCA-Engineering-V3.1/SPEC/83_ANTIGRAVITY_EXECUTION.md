# 83 — Execution Notes for Implementation Agents

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

`ANTIGRAVITY_START_PROMPT.md` is the entry point. This file covers the failure modes observed when
implementation agents work from specifications of this kind.

## Failure mode 1: resolving a conflict silently

Two documents disagree; the agent picks the one it read most recently and proceeds. The result compiles
and passes its own tests. **Correct behaviour: stop and surface the conflict** (D-021, rule 10 of the
start prompt). Every conflict in V2 reached release this way.

## Failure mode 2: inventing an external fact

An API detail is missing, so the agent supplies a plausible one. Plausible is not verified.
**Correct behaviour:** implement the adapter boundary, leave the capability `capabilities_verified: false`,
and record what documentation you actually retrieved with its date (`SPEC/23`).

## Failure mode 3: making a demo pass

A gate blocks the happy path, so the gate is bypassed "temporarily". **Correct behaviour:** the gate is
the feature. A demo that publishes by skipping compliance has demonstrated the opposite of what was asked.

## Failure mode 4: treating the happy path as the work

Failure paths are 70% of this system's value and the first thing dropped under time pressure.
`SPEC/80` schedules them first for that reason.

## Failure mode 5: self-certifying

Writing a document that asserts a phase is complete. **Correct behaviour:** demonstrate the exit criterion
by executing it and report the output (D-029).

## Failure mode 6: editing a generated file

`SPEC/11`, `SPEC/13`, `SCHEMAS/*.json` and `MANIFEST.md` are outputs. Editing them makes the validator
fail on the next run, which is the system working correctly. Edit the generator.

## Checklist before reporting a phase complete

- [ ] `python TOOLS/validate_package.py` green
- [ ] `python TOOLS/conformance_tests.py` green
- [ ] Full test suite green with counts reported
- [ ] The phase exit criterion from `BUILD_ORDER.md` demonstrated by a named test
- [ ] Contracts changed have version bumps
- [ ] Known limitations stated explicitly
- [ ] No conflict left unresolved and unreported
