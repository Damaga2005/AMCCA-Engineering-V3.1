# System Context

## Actors

| Actor | Type | Authority |
|---|---|---|
| Operator | Human | Full. Approves protected actions, changes policy, enables publishing, clears emergency stop. The only actor that can raise a permission. |
| Scheduler | Internal | Enqueues work permitted by policy and budget. Cannot approve anything. |
| Orchestrator | Internal | Sole committer of production state. Applies policy decisions; does not make them. |
| Policy Engine | Internal | Returns ALLOW / REQUIRE_APPROVAL / BLOCK. Has no side effects of its own. |
| Reconciliation Service | Internal | Resolves ambiguous external intents. May move a production out of `UNKNOWN_EXTERNAL_STATE`. |
| Agent Runtime | Internal | Executes agents within their contracts. Holds no authority. |
| Agents | Probabilistic | Propose and report. No authority whatsoever. |

There is deliberately no "administrator" tier below the operator and no service account. A single-user
desktop product that grows a privilege hierarchy has acquired a security model it cannot test.

## External systems

| System | Trust | What we require before relying on it |
|---|---|---|
| AI provider gateway | Untrusted | Live capability probe; recorded model registry entry; request-id capture |
| Publishing platforms | Untrusted | OAuth credential; verified capability row; authoritative status endpoint |
| Research sources | Untrusted | SSRF guard, robots policy, size and time limits, content hash, retrieval timestamp |
| Affiliate/merchant systems | Untrusted | Explicit configuration; validation evidence that is not an HTTP 200 |
| Operating system | Semi-trusted | DPAPI/Credential Manager availability asserted at preflight |
| FFmpeg | Semi-trusted | Presence and version checked at preflight; argument-list invocation only |

"Untrusted" here does not mean hostile. It means: capable of returning something unexpected, of being
unavailable, of changing without notice, and of accepting a request whose outcome we cannot observe.

## Trust boundaries and what crosses them

1. **Operator to system.** Commands are typed and validated. Approvals are scoped, expiring and single-use
   by default. A blanket approval is not representable in the data model.
2. **System to AI provider.** Prompt payloads are minimised. Responses are schema-validated before use.
   Request identifiers are captured for cost reconciliation.
3. **System to platform.** Every mutation is preceded by a committed intent with an idempotency key.
4. **Research source to system.** The single most dangerous inbound path: it fetches attacker-influenceable
   content and feeds it to a model. Handled in `SPEC/28`.
5. **System to disk.** Paths canonicalised, extensions restricted, archives validated before extraction.

## Non-goals

AMCCA does not manage human collaborators, does not offer an API to third parties, does not host content
itself, and does not attempt to model platform algorithms. Each of these was considered and excluded
because it would expand the trust surface faster than it would expand the value.
