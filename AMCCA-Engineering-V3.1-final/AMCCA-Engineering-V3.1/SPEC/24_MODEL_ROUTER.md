# 24 — Model Router

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Selection

For a requested capability, the router selects among `model_registry` rows that are enabled, verified,
unexpired, within constraint limits for the request, and affordable under the reservation. Ordering is by
`fallback_order`, then by cost, then by measured success rate from `provider_health`.

Selection is deterministic given the registry state and the request. It does not consult a model to choose
a model.

## Fallback

On a retryable failure the router may move to the next candidate, but only when the adapter can prove no
side effect occurred. For any operation classified `EXTERNAL_UNSAFE`, an ambiguous failure does not fall
back — it becomes `UNKNOWN_EXTERNAL_STATE` and reconciles.

Falling back after an ambiguous image or video generation is how a budget gets charged twice for one asset.

## Constraints

Each registry entry records hard constraints: maximum input size, maximum output duration, supported
aspect ratios, supported languages, rate class. A request violating a constraint is rejected before
dispatch rather than discovered by the provider.

## Recording

Every routed call records `model_id`, `model_params_hash`, the pricing snapshot used, and the provider
request identifier, in `agent_runs` or `tool_runs`. Without these, neither reproducibility nor cost
reconciliation is possible.

## Degradation

If no candidate is available for a capability, the operation blocks with a clear reason. It does not
silently substitute a different capability, downgrade quality without recording it, or proceed with a
placeholder.
