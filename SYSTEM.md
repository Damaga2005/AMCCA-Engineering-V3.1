# AMCCA System Overview

AMCCA is a local autonomous production system implementing:

`Discover -> Score -> Plan -> Research -> Create -> Verify -> Publish -> Measure -> Learn`

## Four planes

- **Control plane** — policies, policy decisions, approvals, autonomy, budgets, credentials, scheduling, kill switch.
- **Execution plane** — jobs, leases, workers, agents, tools, media rendering, provider and platform adapters.
- **Evidence plane** — sources, claims, rights records, QA reports, artifact lineage, events and audit.
- **Business plane** — niches, opportunities, publications, attribution, revenue, experiments and learning.

**No plane may bypass the control plane for a protected action.** A protected action is any action that
spends money, mutates external state, touches credentials, changes policy or autonomy, or publishes.

## What makes this system different from a content generator

Three properties, each of which costs real engineering effort and each of which is the reason a naive
version of this system eventually publishes something it should not have:

1. **Evidence before assertion.** A factual line in a script traces to a claim, which traces to a source with a
   retrieval timestamp. A model's confidence is not evidence.
2. **Intent before effect.** Every external mutation is written down and committed before it is attempted, so
   that a crash mid-call leaves a record of what might have happened rather than silence.
3. **Estimates are not measurements.** Forecast revenue and confirmed revenue are different tables with
   different constraints. The database refuses to conflate them.

## Trust boundaries

| Boundary | Direction | Validation applied |
|---|---|---|
| Operator -> UI | in | Command validation, approval scope, authorisation |
| Agent -> Orchestrator | in | JSON Schema, tool allow-list, cost ceiling, autonomy ceiling |
| Provider -> Adapter | in | Schema, size limits, timeout, content-type, request-id capture |
| Research source -> Engine | in | SSRF guard, robots policy, size/time limit, content hash, MIME check |
| Adapter -> Platform | out | Intent persisted first, idempotency key, capability verified, policy allow |
| Export -> Filesystem | out | Secret redaction, hash manifest, personal-data exclusion |

Everything crossing a boundary is untrusted until validated, including output from our own agents.
