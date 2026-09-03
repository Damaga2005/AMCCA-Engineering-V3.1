# 81 — Agent Contracts

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

Each agent has a row in `agent_contracts` and an entry here. `SPEC/06` defines the runtime.

## Common fields

`agent_id`, `agent_version`, `input_schema_ref`, `output_schema_ref`, `allowed_tools_json`,
`forbidden_tools_json`, `timeout_seconds`, `max_cost`, `max_autonomy`.

`max_autonomy` caps the agent regardless of the system's autonomy mode. An agent whose `max_autonomy` is
`ASSISTED` still requires approval when the system is `AUTONOMOUS`.

## Catalogue

| Agent | Input | Output | Allowed tools | Deterministic validator |
|---|---|---|---|---|
| `research` | Query plan, niche, constraints | Claim proposals with candidate sources | `research.search`, `research.fetch` (both `READ`) | Source count, independence, recency, trust tier |
| `opportunity` | Trend and niche snapshot | Qualitative assessment | none | Score computed by code, not by the agent |
| `strategy` | Opportunity, claims, memory | Concept options with rationale | none | Target feasibility against media profiles |
| `hook` | Concept, claims | Hook candidates | none | Length, claim mapping, policy screen, similarity |
| `script` | Concept, verified claims, hook | Script document | none | Schema, claim mapping, disclosure presence, duration |
| `storyboard` | Script | Scene plan | none | Coverage, duration reconciliation, safe areas |
| `asset_plan` | Storyboard, media profile | Asset briefs | none | Rights precondition, duplicate screen |
| `qa_vision` | Render, storyboard | Observations | `media.probe` (`READ`) | Findings recorded as `AI_ASSISTED` evidence only |
| `rework_plan` | QA findings, DAG snapshot | Repair proposal | none | DAG resolves the actual target |

Six of nine agents have **no tools at all**. That is the intended shape: an agent is a function from a
document to a document, and tool access is the exception that must be justified per agent.

## Forbidden across every agent

Any tool of class `LOCAL_WRITE`, `EXTERNAL_IDEMPOTENT` or `EXTERNAL_UNSAFE`. No agent publishes, spends
outside its declared ceiling, writes to the database, touches credentials or changes policy. There is no
agent for which an exception is granted.
