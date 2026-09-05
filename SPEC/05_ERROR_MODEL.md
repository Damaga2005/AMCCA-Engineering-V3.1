# 05 — Error Model and Catalogue

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

This file is both the taxonomy and the catalogue. V2 split them and the catalogue covered nine codes
against sixteen categories, so seven classes of failure had no defined handling.

## Code format

`AMCCA-{DOMAIN}-{NNN}` where DOMAIN is two to four uppercase letters. Machine codes are stable
forever; human messages are separate and may be reworded or localised freely.

## Categories and retry disposition

| Category | Retryable | Backoff | Notes |
|---|---|---|---|
| `TRANSIENT` | Yes | Exponential with jitter | Bounded by attempt count and cumulative retry cost |
| `RATE_LIMITED` | Yes | Honour `Retry-After`, else exponential | Counts against provider concurrency, not against attempts |
| `AUTH` | No | — | Moves the account to `REAUTH_REQUIRED`; operator action |
| `CONFIGURATION` | No | — | Startup or preflight abort |
| `VALIDATION` | No | — | Contract violation; regenerate or rework, never retry identically |
| `PROVIDER` | Conditional | Circuit-breaker governed | Retry only if the adapter can prove no side effect occurred |
| `PLATFORM` | Conditional | Circuit-breaker governed | Same proof requirement |
| `MEDIA` | Yes if bounded | Linear | Re-render; a repeated identical failure signature stops the loop |
| `RIGHTS` | No | — | Requires a rights decision, not a retry |
| `COMPLIANCE` | No | — | Requires a disclosure or policy resolution |
| `POLICY` | No | — | Terminal for the attempted action until policy state changes (I-10) |
| `BUDGET` | No | — | Requires an authorised budget change |
| `STORAGE` | Yes if bounded | Linear | Free space or path problem; may resolve after collection |
| `SECURITY` | No | — | Halts the affected capability; never retried automatically |
| `USER_ACTION_REQUIRED` | No | — | Blocks with a notification explaining the required action |
| `UNKNOWN_EXTERNAL_STATE` | **Never blindly** | — | Reconcile first; retry only if reconciliation proves no side effect |
| `INTERNAL` | No | — | Bug. Fails the job, preserves state, raises a CRITICAL notification |

## Catalogue

| Code | Category | Retry | Operator action |
|---|---|---|---|
| `AMCCA-CFG-001` | CONFIGURATION | No | Configuration failed schema validation |
| `AMCCA-CFG-004` | CONFIGURATION | No | Budget window consistency rule violated |
| `AMCCA-SEC-001` | SECURITY | No | Security policy block; review and resolve |
| `AMCCA-SEC-002` | SECURITY | No | Literal credential found in configuration; move to the secret store |
| `AMCCA-SEC-003` | SECURITY | No | SSRF guard rejected a research target |
| `AMCCA-SEC-004` | SECURITY | No | Archive rejected: entry count, size or path validation failed |
| `AMCCA-DB-001` | INTERNAL | No | Foreign keys or WAL not enabled |
| `AMCCA-DB-002` | CONFIGURATION | No | Migration checksum mismatch; do not run on this database |
| `AMCCA-DB-003` | TRANSIENT | Yes | SQLite busy beyond `busy_timeout` |
| `AMCCA-STM-001` | INTERNAL | No | Attempted a transition absent from `SPEC/13` |
| `AMCCA-STM-002` | INTERNAL | No | Resume attempted to a state other than `blocked_from` |
| `AMCCA-STM-003` | INTERNAL | No | Outbound transition attempted from a terminal state |
| `AMCCA-JOB-001` | TRANSIENT | Yes | Lease expired mid-execution; fence token stale, work abandoned |
| `AMCCA-JOB-002` | INTERNAL | No | Duplicate idempotency key on enqueue. `EnqueueJobAsync` lets the `UNIQUE(idempotency_key)` constraint reject the second insert (no check-then-act pre-check — unsound under concurrency, SPEC/15) and wraps that `SqliteException` in this code. A collision means the same logical intent was enqueued twice; the caller acts on the existing job. |
| `AMCCA-JOB-003` | USER_ACTION_REQUIRED | No | Job dead-lettered after max attempts |
| `AMCCA-ORC-001` | USER_ACTION_REQUIRED | No | The orchestrator reached a production state with no registered stage handler and moved the production to `BLOCKED` for an operator. Carried as the transition `reason_code`, not thrown — an operator supplies the stage result or a handler is added. |
| `AMCCA-ORC-002` | USER_ACTION_REQUIRED | No | A stage handler threw while the orchestrator was driving a production; the orchestrator moved it to `BLOCKED` carrying the handler's failure. Carried as the transition `reason_code`, not thrown. |
| `AMCCA-AI-001` | PROVIDER | Conditional | Gateway error; check provider health |
| `AMCCA-AI-002` | UNKNOWN_EXTERNAL_STATE | No | Reconcile before any retry |
| `AMCCA-AI-003` | VALIDATION | No | Agent output failed its declared schema |
| `AMCCA-AI-004` | POLICY | No | Agent attempted a forbidden tool |
| `AMCCA-AI-005` | BUDGET | No | Reserved; not currently thrown. Its cost-ceiling half is a duplicate of `AMCCA-BUD-002` (`AgentRuntime.ExecuteToolCallAsync` throws that instead, DEF-004); its timeout half deliberately surfaces as a raw, unwrapped `OperationCanceledException` — an established, tested contract (`TimeoutSeconds_CancelsExecutionWhenExceeded`) that matches .NET's own cancellation convention and should not be wrapped. |
| `AMCCA-RES-001` | VALIDATION | No | Material claim lacks sufficient independent sources |
| `AMCCA-RES-002` | TRANSIENT | Yes | Research source unavailable |
| `AMCCA-RES-003` | SECURITY | No | Reserved; duplicate of `AMCCA-SEC-003` (`SsrfValidator` throws `AMCCA-SEC-003` for every SSRF/domain-policy rejection). Not currently thrown by any code path — kept catalogued rather than removed so a future caller cannot silently reuse the code for an unrelated condition. |
| `AMCCA-MED-001` | MEDIA | Yes if bounded | Render failed; re-render |
| `AMCCA-MED-002` | MEDIA | No | FFmpeg timeout or output ceiling exceeded |
| `AMCCA-QA-001` | VALIDATION | No | QA failure; rework |
| `AMCCA-QA-002` | INTERNAL | No | AI-assisted finding attempted to set a verdict |
| `AMCCA-QA-003` | INTERNAL | No | Named QA threshold-profile lookup failed. `QaThresholdProfileRegistry.Resolve` throws it for an unknown `threshold_profile_id`; the constructor throws it for a stricter profile that lowers a threshold below the base (SPEC/35: a profile may raise thresholds, never lower them). `qa_reports` has no production writer yet, so the profile id is an in-memory `QaVerdictEvaluator` input today; a persisted `threshold_profiles` table keyed by the ULID `qa.schema.json` describes is deferred until QA verdicts are recorded. |
| `AMCCA-RGT-001` | RIGHTS | No | Asset not GREEN; review rights |
| `AMCCA-CMP-001` | COMPLIANCE | No | Required synthetic-content label not applied |
| `AMCCA-CMP-002` | COMPLIANCE | No | Required affiliate disclosure missing |
| `AMCCA-PLT-001` | PLATFORM | Conditional | Platform rejected the request |
| `AMCCA-PLT-002` | AUTH | No | Credential invalid or expired; re-authenticate |
| `AMCCA-PLT-003` | RATE_LIMITED | Yes | Platform rate limit |
| `AMCCA-PUB-001` | PLATFORM | Conditional | Publication attempt failed |
| `AMCCA-PUB-007` | UNKNOWN_EXTERNAL_STATE | No | Publication outcome unknown; reconcile |
| `AMCCA-PUB-008` | POLICY | No | Duplicate publication prevented by unique constraint |
| `AMCCA-POL-001` | POLICY | No | Policy evaluation rejected or failed; required decision data is missing |
| `AMCCA-POL-003` | POLICY | No | Refused: global or per-platform kill switch is active; clear it to proceed |
| `AMCCA-POL-004` | USER_ACTION_REQUIRED | No | Human approval required before the protected action; request and grant one |
| `AMCCA-BUD-001` | BUDGET | No | Budget threshold reached |
| `AMCCA-BUD-002` | BUDGET | No | Reservation refused; insufficient remaining budget |
| `AMCCA-STO-001` | STORAGE | Yes if bounded | Insufficient free space |
| `AMCCA-STO-002` | STORAGE | No | Artifact file missing for an existing version row |
| `AMCCA-REF-001` | VALIDATION | No | Referral validation insufficient for ACTIVE |
| `AMCCA-INT-001` | INTERNAL | No | Unhandled internal error |

## Rules

1. A code is never reused for a different meaning. Retire, never repurpose.
2. Every code appearing in `jobs.last_error_code` or `publications.last_error_code` MUST exist here;
   the validator checks that the pattern is satisfied and a test checks membership.
3. An error whose category is `UNKNOWN_EXTERNAL_STATE` is never counted as a failure for retry purposes,
   because it is not known to have failed.
