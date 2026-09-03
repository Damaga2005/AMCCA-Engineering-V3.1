# 64 — Internal Application API

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Shape

Typed commands and queries between the UI layer and the application layer, in-process. Commands are
imperative and validated; queries are read-only and side-effect free.

## Command contract

Every command declares: its name, its input type, its required permissions, whether it is a protected
action, its idempotency behaviour and its failure codes. A protected command is routed through the policy
engine before execution, without exception.

## Query contract

Queries never mutate. A query that needs to record that it ran is not a query; it is a command with a
result. This distinction is enforced by convention plus a test that asserts no query handler opens a
write transaction.

## Command catalogue (representative)

`CreateProduction`, `CancelProduction`, `ApproveAction`, `RejectAction`, `RetryJob`,
`ReconcileIntent`, `PublishProduction`, `RetractPublication`, `ConnectAccount`, `DisconnectAccount`,
`VerifyCapabilities`, `SetAutonomyMode`, `SetKillSwitch`, `ActivatePolicyVersion`, `EnableModel`,
`AddReferralProgram`, `ExportProduction`, `ImportPackage`, `RunRetention`, `CreateBackup`, `RestoreBackup`.

Every command in that list except the queries-in-disguise is a protected action. That is the point:
the list of things the UI can do is the list of things the policy engine sees.

## Error contract

Commands return a result carrying either a value or an error code from `SPEC/05`. Exceptions are not the
error channel across this boundary; an unexpected exception is mapped to `AMCCA-INT-001` and logged with
its correlation id.
