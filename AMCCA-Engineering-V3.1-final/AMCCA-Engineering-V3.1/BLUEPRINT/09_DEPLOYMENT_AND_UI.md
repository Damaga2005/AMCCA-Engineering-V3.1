# Deployment and Control Center

## Deployment

Self-contained .NET 8 publish for Windows x64. WiX installer. Release artifacts versioned and SHA-256
hashed; production releases code-signed.

Data lives under the configured `data_root` (default `%LOCALAPPDATA%/AMCCA/data`), which contains the
SQLite database, the artifact store, logs, backups and exports. Uninstall preserves the data directory by
default; removing it is an explicit, separately confirmed action.

Upgrade path: backup, migrate forward with checksum verification, verify, start. A failed migration
restores the pre-migration backup automatically and refuses to start rather than running on a
half-migrated database.

## Control Center screens

| Screen | Answers |
|---|---|
| Dashboard | What is running, what is blocked, what is spending, what needs me |
| Productions | Every production, its state, its gate history, its cost |
| Production inspector | Full lineage: sources, claims, artifacts, QA findings, policy decisions, events |
| Job queue | What is queued, leased, retrying, dead-lettered, and why |
| Approvals | What is waiting on me, with the exact scope of what I would be approving |
| Publications | Per-target state, evidence, external URL, synthetic label status |
| Money | Reservations, settled costs, confirmed revenue, profit over confirmed revenue only |
| Evidence | Sources and claims with retrieval timestamps and trust tiers |
| Policies | Active policy versions and recent decisions |
| Providers | Model registry, capability verification status and age |
| Security | Secret references (never values), credential states, redaction self-test |
| Safety | Kill switch, autonomy mode, publishing state — reachable from every screen |

## Interface obligations

1. The kill switch is reachable in one action from anywhere.
2. Any blocked item explains which rule blocked it and what would unblock it.
3. Estimated and measured values are visually distinct and never presented as interchangeable.
4. An approval dialog shows the exact scope, cost ceiling and expiry of what is being approved.
5. Autonomy mode and publishing state are always visible, never buried in settings.
6. No screen shows a number without provenance.

Rule 3 exists because the most expensive mistake an operator of this system can make is to believe a
forecast is a measurement, and interface design is where that belief is either prevented or created.
