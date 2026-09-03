# 50 — Security

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Threat model

The realistic threats to a local autonomous publishing system, in rough order of likelihood:

1. **Prompt injection through retrieved research content.** Handled in `SPEC/28`. The structural defence
   is that tool permissions come from the agent contract, not from anything a document can say.
2. **Credential exfiltration through logs, exports or diagnostics bundles.** Handled by redaction,
   `secret://` constraints and a dedicated test suite.
3. **SSRF through research fetching.** Handled by resolved-address validation and redirect re-checking.
4. **Malicious media or archive input.** Handled by isolation, limits and archive validation.
5. **Runaway autonomous spend.** Handled by atomic budget reservation and thresholds.
6. **Unintended publication.** Handled by preflight gates, intents, locks and unique constraints.
7. **Supply chain compromise.** Handled by pinned dependencies and advisory scanning (`SPEC/59`).

Note what is *not* on this list: multi-tenant isolation and network authentication. D-001 keeps this a
single-user local product precisely so those threats stay out of scope. Adding a remote backend would
change this model materially and requires an ADR.

## Controls

| Area | Control |
|---|---|
| Secrets | DPAPI / Credential Manager; `secret://` references only; `CHECK` constraints in storage |
| Logging | Redaction middleware ahead of every sink; verified by test |
| Filesystem | Canonicalisation, traversal rejection, extension allow-list, confinement under `data_root` |
| Archives | Entry count, uncompressed size and per-entry path validation (`AMCCA-SEC-004`) |
| Process | FFmpeg via argument list, never a shell; timeouts and output ceilings |
| Network | HTTPS enforced; redirects revalidated; resolved-IP allow-listing for research |
| Agents | Least privilege via tool allow-lists; no database handle, no filesystem handle |
| Data at rest | OS-level protection assumed; the database holds no secret values |
| Exports | Redaction and personal-data exclusion applied before packaging |

## Least privilege for agents

An agent's capability is exactly the intersection of its contract's `allowed_tools` and the permissions
policy grants at invocation time. There is no ambient authority. This is what makes a successful prompt
injection a failed proposal rather than an action.

## Incident behaviour

A `SECURITY`-class error halts the affected capability immediately, raises a `CRITICAL` notification, and
is never auto-retried. It does not degrade gracefully — a security control that fails open is not a
control.
