# Security, Observability and Testing

## Security boundaries

| Surface | Control |
|---|---|
| Credentials | DPAPI / Credential Manager behind `ISecretStore`; only `secret://` references persisted |
| Configuration | Schema-validated at startup; a literal credential aborts startup |
| Filesystem | Canonical paths, traversal rejection, extension allow-list, confined artifact root |
| Archives | Entry count, total size and path validation before extraction |
| Media | FFmpeg via argument list, never a shell; timeouts and output ceilings |
| Network | HTTPS enforced except explicitly configured localhost; redirects revalidated |
| Research fetch | SSRF guard: resolved-IP allow-listing, private-range rejection, redirect re-checking |
| Logs | Redaction middleware ahead of every sink; verified by test, not by convention |
| Agents | Least privilege via tool allow-lists; no arbitrary SQL, no filesystem handle |
| Exports | Secret redaction and personal-data exclusion applied before packaging |

The research fetch path deserves its own emphasis: it retrieves content an adversary can influence and
feeds it to a language model that will act on it. Prompt-injection resistance is a design requirement
there, not a nice-to-have (`SPEC/28`).

## Observability

Structured logs with correlation identifiers. Metrics for job throughput, lease expiry, retry rates,
provider latency and error rates, budget utilisation, QA pass rates, reconciliation backlog and
publication verification lag.

Every operator-visible number carries its provenance. Every blocked item carries the rule that blocked it
and the policy version that rule came from. "Something went wrong" is not an acceptable terminal state
for anything the operator can see.

## Testing philosophy

Layers: unit, contract, integration, media, security, concurrency, recovery, chaos, end-to-end, packaging.

Two rules that matter more than coverage percentage:

1. **A fake must never hide an integration bug.** Provider and platform fakes must model success, timeout,
   429, 5xx, malformed body, partial write and ambiguous side effect. A fake that only models success is
   worse than no fake, because it produces confidence.
2. **Every guarantee in `10_OPERATIONAL_INVARIANTS.md` has a test that tries to break it.** An invariant
   with no adversarial test is a hope.

Required acceptance properties: no lost durable state; no duplicate publication; no false success; no
unlabelled synthetic publication; verified artifacts survive; failed operations are retryable where and
only where that is safe; retries are bounded; the audit trail is complete.
