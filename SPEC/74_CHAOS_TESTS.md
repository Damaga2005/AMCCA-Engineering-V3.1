# 74 — Chaos Test Suite

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Method

Inject failures at defined checkpoints, kill the process, restart, and assert consistency and
resumability. Every scenario runs against a seeded database with a deterministic clock.

| # | Scenario | Assertion after restart |
|---|---|---|
| X-01 | Kill during research fetch | No partial claim; retryable; no orphan source rows |
| X-02 | Kill during script generation | Agent run recorded as incomplete; no invalid artifact version |
| X-03 | Kill during render, before hashing | Temp file collected; no version row referencing it |
| X-04 | Kill after render, before version commit | Temp file collected; render repeatable |
| X-05 | Kill after intent commit, before the external call | Intent `CREATED`; reconciliation determines it never dispatched; safe to retry |
| X-06 | Kill after the external call, before recording the response | Intent `UNKNOWN`; **no retry**; reconciliation resolves |
| X-07 | Timeout after upload submission | `UNKNOWN_EXTERNAL_STATE`; no second upload; reconciliation via `ListRecent` |
| X-08 | Platform returns 200 to upload, then 404 on status | Publication does not reach `VERIFIED`; blocks after reconcile exhaustion |
| X-09 | Kill during budget settlement | Reservation still held; settlement idempotent on replay |
| X-10 | Kill during manifest sealing | Manifest unsealed; production not `FINAL_VERIFIED`; resumable |
| X-11 | Kill during migration | `PRE_MIGRATION` backup restored; refuses to start; reports the version |
| X-12 | Disk fills mid-render | Render fails cleanly; no partial artifact accepted; scheduler stops dispatching renders |
| X-13 | Provider returns malformed JSON for a paid call | Cost recorded; output rejected; no invalid artifact |
| X-14 | Provider 429 storm | Circuit opens; reconciliation still runs; no unbounded retry |
| X-15 | Missing clean-shutdown marker | Full recovery sweep runs, not the fast path |
| X-16 | Artifact file deleted out of band | Version tombstoned with `AMCCA-STO-002`; production blocked, not silently republished |

## Acceptance

Across every scenario: no lost durable state, no duplicate publication, no false success, no unlabelled
synthetic publication, no secret exposure, and every ambiguity resolvable or explicitly blocked with a
notification.

X-06 and X-07 are the two scenarios this entire architecture exists to survive. If they pass and nothing
else does, the system is still safe. If everything else passes and they do not, it is not.
