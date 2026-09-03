# AMCCA implementation rules

Read `DECISIONS.md`, `BLUEPRINT/10_OPERATIONAL_INVARIANTS.md` and the relevant SPEC files before changing code.

## Never do

- Do not invent provider or platform APIs, routes, headers or capabilities.
- Do not mark an external operation successful without authoritative evidence.
- Do not let an agent write arbitrary database rows.
- Do not store secrets in source, JSON, YAML, logs, screenshots or exports.
- Do not bypass QA, rights, disclosure, budget or policy gates to make a demo pass.
- Do not replace a failing integration with a fake success adapter.
- Do not use floating point for money.
- Do not hand-edit a generated artifact (`SPEC/11`, `SPEC/13`, `SCHEMAS/*.json`, `MANIFEST.md`).
- Do not add a framework or a required external service without an ADR in `DECISIONS.md`.
- Do not resolve a contradiction between two documents by picking one. Stop and surface it.

## Required implementation loop

1. Identify the contract (schema or SPEC section).
2. Implement deterministic domain behaviour first.
3. Add the adapter boundary behind a port.
4. Add schema validation at the boundary.
5. Add unit and integration tests.
6. Add failure and recovery tests — crash, timeout, ambiguous response.
7. Update the contract and its version.
8. Run `python TOOLS/validate_package.py` and the full test suite.

Step 6 is not optional and is not last-minute. A path that has never been tested failing is a path that
does not work, and in this system the failure paths are where the money and the reputation live.

## When you are unsure

Uncertainty about an external system is normal and is handled by writing an adapter boundary and leaving
the capability disabled with `capabilities_verified=false`. Uncertainty about an internal contract is not
normal: it means the contract is missing, and a missing contract is a specification bug to be raised,
not a gap to be filled with a guess.
