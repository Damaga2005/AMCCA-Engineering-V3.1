# Agent implementation contract

Every agent MUST declare, in `agent_contracts` and in `SPEC/81`:

`agent_id`, `agent_version`, purpose, input schema ref, output schema ref, allowed tools, forbidden tools,
timeout, retry policy, max cost, max autonomy, deterministic validators, failure codes, observability fields.

## What an agent is

An agent is a function from a validated input document to a validated output document. It is not an actor.
It has no ambient authority, no database handle, no filesystem handle and no network handle other than the
tools its contract grants it.

## What an agent may never do

- Mutate persistent state directly.
- Decide that a policy, budget, rights, disclosure or QA check has passed.
- Elevate its own autonomy, budget, tool set or timeout.
- Call a tool absent from its `allowed_tools` list.
- Return prose where the contract specifies a document.

`audit_log.actor_type` deliberately has no `AGENT` value. An agent is never the authority for a protected
action, so it can never be the actor recorded for one.

## Reproducibility

An agent MUST be deterministic with respect to its inputs as far as the selected model permits.
`agent_runs` records `agent_version`, `prompt_version_id`, `model_id`, `model_params_hash` and `input_hash`
so that a run can be replayed and compared. Sampling parameters are recorded, never left to a default.

## Output handling

1. Agent returns a document.
2. Orchestrator validates it against the declared output schema. Failure sets `output_valid=false` and state
   `VALIDATION_FAILED`; it does not retry with a different prompt hoping for better luck.
3. Orchestrator applies policy.
4. Orchestrator persists state and performs side effects through tools.

Note step 2 is not "the agent reports success". `agent_runs.output_valid` is written by the validator,
because a component is never the judge of its own output.
