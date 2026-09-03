# Architecture

```text
                         WPF UI  (MVVM, Control Center)
                                    |
                          Application Services (typed commands)
                                    |
   +--------------------------------+--------------------------------+
   |                                |                                |
Policy Engine                 Orchestrator                     Cost Engine
(allow/approve/block)   (sole committer of production state)  (reserve/settle)
   |                                |                                |
   +--------------------------------+--------------------------------+
                                    |
                        Job Scheduler  --  Lease Manager
                                    |
                              Worker Pool
                 |              |              |             |
          Agent Runtime   Media Worker   Integration     Reconciliation
                 |         -> FFmpeg       Workers          Service
                 |              |              |             |
          Tool Registry (side-effect classes, permissions, idempotency)
                                    |
              +---------------------+---------------------+
              |                     |                     |
       SQLite (WAL)          Artifact Store         Event + Audit Store
       metadata only       hash-addressed files      append-only
                                    |
   External boundary (all through ports, never referenced directly above this line):
   IProviderGateway | IPlatformAdapter | IResearchSource | IAffiliateProvider | ISecretStore
```

## Rules this diagram encodes

1. **The Orchestrator is the only component that commits production state.** Not workers, not agents, not the UI.
   Every state change passes through it and lands in one transaction with its event and transition record.
2. **The Policy Engine sits beside the Orchestrator, not inside the workers.** A worker cannot decide it is allowed.
3. **Ports, not vendors.** Nothing above the adapter line names OmniRouters, YouTube or FFmpeg. Substituting
   a provider is an adapter change (D-013).
4. **External calls never happen inside a database transaction.** The intent is committed, the transaction closes,
   then the call is made. This is what makes crash recovery decidable.
5. **The artifact store is hash-addressed.** Identity is content, so a duplicate render is detectable and an
   altered file is detectable.

## Startup sequence

1. Load configuration, validate against `SCHEMAS/config.schema.json`. Failure aborts.
2. Open SQLite, assert `foreign_keys=ON` and WAL. Failure aborts.
3. Back up the database, then apply migrations in order, verifying each checksum.
4. Load kill-switch state from the database. `EMERGENCY_STOP` survives restart by construction.
5. Run preflight (`SPEC/49`): secrets reachable, disk headroom, FFmpeg present and version-checked, clock sane.
6. Run recovery (`SPEC/16`): expired leases, incomplete artifact writes, `UNKNOWN` intents, unreconciled publications.
7. Only then start the scheduler. **Recovery precedes scheduling; a system that starts new work before
   reconciling old ambiguity is how duplicate publications happen.**

## Shutdown sequence

Stop accepting new jobs, signal workers, allow the configured grace period, checkpoint WAL, release leases
explicitly rather than letting them expire, persist a clean-shutdown marker. An absent marker at next start
escalates the recovery pass.
