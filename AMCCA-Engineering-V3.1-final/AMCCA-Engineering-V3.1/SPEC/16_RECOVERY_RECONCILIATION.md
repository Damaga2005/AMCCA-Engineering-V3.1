# 16 — Recovery and Reconciliation

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Startup recovery, before the scheduler starts

| Scan | Action |
|---|---|
| Jobs with expired leases | Validate fence token; requeue or dead-letter |
| Jobs in `RUNNING` with no live worker | Requeue if no unsafe intent outstanding; else move to `UNKNOWN_EXTERNAL_STATE` |
| `intents` in `DISPATCHED` or `UNKNOWN` | Enqueue P0 reconciliation |
| Temp artifact files with no committed version | Delete |
| Version rows with missing files | Mark `TOMBSTONED`, raise `AMCCA-STO-002` |
| Publications in non-terminal states | Re-verify against authoritative status |
| Productions in `BLOCKED` without `blocked_from` | Integrity error; CRITICAL notification |
| Missing clean-shutdown marker | Escalate to the full sweep rather than the fast path |

## Recovery classes

| Class | Handling |
|---|---|
| `TRANSIENT` | Retry with bounded exponential backoff |
| `PERMANENT` | Fail the job; preserve state and evidence |
| `USER_ACTION` | Block with a notification naming the required action |
| `UNKNOWN` | Reconcile before anything else |
| `SECURITY` | Halt the affected capability; never auto-retry |

## Reconciliation

Each adapter implements a status lookup where the external system supports one. Reconciliation runs
periodically and is also event-driven immediately after any uncertain operation.

Resolution order for an unknown publication:
1. Query by provider request id, if captured.
2. Query by external id, if one was returned before the loss.
3. List recent items for the account and match on request fingerprint and content hash.
4. If steps 1-3 are inconclusive after `policy.reconcile.max_attempts`, move to `BLOCKED` and notify.

Step 4 is the honest outcome. A reconciler that eventually guesses is worse than one that stops, because
it converts a visible problem into an invisible one.

## No false success

A publication is `VERIFIED` only from authoritative platform evidence, with `evidence_source` and
`evidence_retrieved_at` recorded. An upload accepted for processing is not published. A 200 response is
not evidence. This is enforced by a database `CHECK`, so it survives a careless code path.
