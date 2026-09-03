# 06 — Agent System

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## The runtime

The agent runtime loads an agent contract from `agent_contracts`, resolves the pinned
`prompt_version_id` and `model_id`, builds the input document, validates it against the declared input
schema, invokes the gateway, validates the output against the declared output schema, and returns a
typed result to the calling engine. It writes exactly one `agent_runs` row per invocation.

It holds no database handle for domain tables, no filesystem handle and no network handle other than
the gateway port. Everything else an agent needs, it gets through a tool.

## Agent catalogue

| Agent | Proposes | Deterministic validator |
|---|---|---|
| ResearchAgent | Claims with candidate sources | Source count, independence, recency, trust tier |
| OpportunityAgent | Qualitative reads of a niche | Scoring is deterministic; the agent never produces the score |
| StrategyAgent | Concept options | Selection recorded as an operator or policy decision |
| HookAgent | Hook candidates | Length, language, policy screen |
| ScriptAgent | Script versions | Schema, claim mapping, content policy |
| StoryboardAgent | Scene plans | Scene-to-script coverage, duration reconciliation |
| AssetPlanAgent | Asset briefs | Rights precondition, duplicate screen |
| QaVisionAgent | Visual and content observations | Evidence only; `check_kind = AI_ASSISTED` |
| ReworkPlanAgent | Repair proposals | DAG resolves the actual target, not the agent |

Note the pattern: in every row, the agent supplies judgement and the validator supplies authority.
The two are never the same component.

## Cost and time bounds

Every agent contract declares `timeout_seconds` and `max_cost`. Both are enforced by the runtime before
the call, not discovered afterwards. Exceeding either produces `AMCCA-AI-005` and does not retry with a
larger allowance.

## Failure handling

- Schema validation failure: `AMCCA-AI-003`, run state `VALIDATION_FAILED`. The runtime does not reword
  the prompt and try again hoping for a better roll. One bounded retry with identical inputs is permitted
  for `TRANSIENT` provider errors only.
- Forbidden tool: `AMCCA-AI-004`, run state `BLOCKED`, audited.
- Timeout after dispatch: run state `UNKNOWN_EXTERNAL_STATE` if the call may have consumed budget or
  produced a side effect; reconciled through the provider request id.

## Determinism

Sampling parameters are set explicitly and hashed into `model_params_hash`. Nothing relies on a provider
default, because a provider default is a value that can change without notice and take reproducibility
with it.
