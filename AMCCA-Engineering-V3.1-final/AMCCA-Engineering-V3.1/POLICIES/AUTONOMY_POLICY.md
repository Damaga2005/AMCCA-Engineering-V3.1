# Autonomy Policy

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

The autonomy matrix in `BLUEPRINT/05_AUTONOMY_POLICY_APPROVALS.md` is normative and is not restated here.
This file states the rules that govern the matrix itself.

## Rules

1. Autonomy is a permission set granted by an operator, never a property an agent holds.
2. Only an operator can raise autonomy. The change is audited with identity and timestamp.
3. An agent's `max_autonomy` caps it regardless of system mode. The effective autonomy for an action is
   the minimum of system mode, action matrix entry and agent ceiling.
4. The system may lower its own effective autonomy and never raise it. Degradation triggers are listed
   in `SPEC/53`.
5. Four capabilities are blocked in every mode with no approval path: increasing own budget, disabling QA,
   bypassing the kill switch, and skipping a required synthetic-content label.
6. Raising to `AUTONOMOUS` requires: `providers.gateway.capabilities_verified = true`, a second
   `IProviderGateway` implementation present (D-013), and every suite in `SPEC/72`-`SPEC/74` green.

## Rationale for rule 5

Each of the four is a case where the cost of being wrong is unbounded and the value of autonomous
judgement is close to zero. A system that can raise its own budget has no budget. A system that can skip a
disclosure gate has no disclosure gate. Making these approvable rather than blocked would mean the
protection lasts exactly until someone is in a hurry.
