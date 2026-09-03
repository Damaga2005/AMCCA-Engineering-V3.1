# State and Data Flow

## Where state lives

| Kind of state | Home | Why there |
|---|---|---|
| Production lifecycle | `productions.state` + `state_transitions` | Single committed current state, full history |
| Durable work | `jobs`, `job_attempts`, `leases` | Survives restart; leases prevent double execution |
| External ambiguity | `intents`, `reconciliation_attempts` | A crash mid-call leaves a record, not silence |
| Content | `artifacts`, `artifact_versions`, `artifact_edges` | Immutable versions plus a lineage DAG |
| Evidence | `sources`, `claims`, `claim_sources`, `rights_records`, `qa_reports` | Reconstructable justification |
| History | `events` (append-only), `audit_log` (separate) | What happened vs who was allowed |
| Money | `budgets`, `budget_reservations`, `cost_events`, `revenue_events` | Reservation, settlement and revenue kept distinct |
| Files | Artifact store, hash-addressed | SQLite holds metadata, never media (D-017) |

## The three-column rule

`productions` carries `blocked_from`, `unknown_from` and `rework_attempts` for one reason: without them,
the resume, reconcile and rework-exhaustion transitions are not decidable. A state machine whose exit
conditions depend on information nobody wrote down is not a state machine.

## Data lineage

Every artifact version points at the versions it was derived from. The resulting DAG is what makes
targeted rework possible: a QA finding names a responsible artifact version, `DagService` walks the
descendants, marks them `SUPERSEDED` or `INVALIDATED` without deleting, and rework regenerates the
earliest repairable ancestor. Research is not regenerated for a render defect unless an edge proves it
is affected.

Cycles are rejected at insert. A cyclic lineage graph would make invalidation non-terminating.

## SQLite and filesystem separation

SQLite holds identifiers, hashes, sizes and relative paths. The artifact store holds bytes. Consequences
that must be handled and are:

- A file present on disk with no `artifact_versions` row is orphaned; the retention job collects it.
- A row whose file is missing is a corruption; recovery detects it at startup and marks the version `TOMBSTONED`.
- Export verifies every hash before packaging. Import verifies every hash before accepting.

## Provenance of numbers

Every number the operator sees is labelled with where it came from. `analytics_snapshots.provenance` and
`revenue_events.provenance` are mandatory columns, and the unique key on analytics includes provenance so
that a measured value and an estimate can coexist without one silently overwriting the other. The read
path always prefers `API_MEASURED`.
