# 80 — Implementation Plan

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

`BUILD_ORDER.md` holds the binding phase sequence and exit criteria. This file adds the working method.

## Per phase

1. Read the relevant SPEC files and the schemas they reference. Do not start from memory of V2.
2. Write the contract tests first, including the negative instances. A contract without a failing negative
   test is not enforced.
3. Implement deterministic domain behaviour.
4. Add the adapter boundary behind a port.
5. Add validation at the boundary.
6. Add failure and recovery tests — crash, timeout, ambiguous response.
7. Run `TOOLS/validate_package.py` and the full suite.
8. Demonstrate the phase exit criterion by executing it.

## Order within a phase

Domain before adapters. Contracts before implementations. Failure paths before optimisation. The failure
paths are where this system's value lives, and they are the first thing dropped when a phase runs late,
so they are scheduled first.

## Definition of a blocked phase

A phase is blocked, not slow, when: a SPEC file is ambiguous, two documents conflict, an external
capability cannot be verified, or a required contract does not exist. In all four cases the correct action
is to surface it, not to invent a resolution (`ANTIGRAVITY_START_PROMPT.md`, rule 10).

## Reporting

Per phase: files changed, contracts changed with version bumps, migrations applied, tests run with counts,
validator result, known limitations, and an explicit statement of whether the exit criterion was
demonstrated. "Implemented" is not a status; "exit criterion demonstrated by test X" is.
