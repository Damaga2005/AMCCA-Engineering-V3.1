# 49 — Preflight Gates

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

Two distinct preflights: system startup and publication.

## System preflight (at startup, before recovery)

| # | Check | Failure |
|---|---|---|
| 1 | Configuration validates against `config.schema.json` | `AMCCA-CFG-001`, abort |
| 2 | No literal credential in configuration | `AMCCA-SEC-002`, abort |
| 3 | Budget consistency rules (`SPEC/03`) | `AMCCA-CFG-004`, abort |
| 4 | Database opens; WAL and `foreign_keys` on | `AMCCA-DB-001`, abort |
| 5 | Migrations current and checksums match | `AMCCA-DB-002`, abort |
| 6 | Secret store reachable | Abort |
| 7 | `data_root` writable, free space above minimum | `AMCCA-STO-001`, degraded start |
| 8 | FFmpeg present and version within the supported range | Degraded start; media disabled |
| 9 | System clock plausible against last recorded event time | Warn; lease logic depends on it |
| 10 | Kill-switch state loaded | Halted start if `EMERGENCY_STOP` |

Check 9 exists because leases, retention and budget windows are all clock-dependent. A clock that has
jumped backwards can expire leases early and reopen budget windows, and both are silent failures.

## Publication preflight (per target, before dispatch)

| # | Check | Failure |
|---|---|---|
| 1 | Production is `FINAL_VERIFIED` with a sealed manifest | Block |
| 2 | Every QA stage verdict is `PASS` | `AMCCA-QA-001` |
| 3 | Every asset rights record is `GREEN` and unexpired | `AMCCA-RGT-001` |
| 4 | Required affiliate disclosure present and correctly placed | `AMCCA-CMP-002` |
| 5 | Synthetic declaration complete and consistent with the artifact DAG | `AMCCA-CMP-001` |
| 6 | If a label is required, `apply_synthetic_label` is `VERIFIED` for this target | `AMCCA-CMP-001` |
| 7 | Platform capability `VERIFIED` and unexpired for every capability used | Block |
| 8 | Account state `CONNECTED`, credential valid | `AMCCA-PLT-002` |
| 9 | Media profile matches the target's requirements | Block |
| 10 | Referral links `ACTIVE`, unexpired, permitted in target geography and platform | `AMCCA-REF-001` |
| 11 | Budget reservable | `AMCCA-BUD-002` |
| 12 | Kill switch permits publishing; `publishing_enabled` is true | Block |
| 13 | No existing publication row for this exact target and content version | `AMCCA-PUB-008` |
| 14 | Approval present, unexpired and unconsumed if the autonomy mode requires one | Block |

Any failure blocks the target. It does not warn and proceed, and it does not proceed for the other
targets in a way that hides the blocked one — the operator sees a per-target result.
