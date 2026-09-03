# 61 — UI Flows

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## First run

Welcome, data directory selection, configuration validation, secret store availability check, FFmpeg
detection, then a safety summary showing that publishing is disabled, autonomy is `MANUAL` and `dry_run`
is on. The operator is told what is off, not asked to discover it (D-020).

No credential is requested during first run. Connecting an account is a later, deliberate action.

## Connect a platform account

Explain the scopes about to be requested and why -> OAuth with PKCE (`SPEC/43`) -> identity probe ->
capability probe -> show the resulting capability matrix, including anything `UNSUPPORTED`. The operator
sees what the account can and cannot do before relying on it.

## Create a production

Select or accept an opportunity, showing its score breakdown -> confirm concept, disclosure requirements
and target platforms -> confirm budget -> start. Disclosure requirements are shown at this point, before
any cost is incurred, because that is the cheapest moment to discover them.

## Approve an action

Notification -> approval screen showing action, subject, cost ceiling, expiry, the policy rule requiring
approval, and the evidence relevant to the decision -> approve or reject with an optional reason -> audit.

## Investigate a block

From any blocked item: the rule, the policy version, the inputs hash, the decision timestamp, and the
specific remediation. From there, a direct link to the affected artifact, claim or capability.

## Handle an unknown external state

The screen states plainly that the outcome is unknown and that the system will not retry until it is
resolved. It shows reconciliation attempts, what each one checked, and what evidence would resolve it.
The operator may trigger a reconciliation attempt or, after exhausting them, record a manual confirmation
which is audited as `OPERATOR_CONFIRMATION` evidence.

## Emergency stop

One action from anywhere. Confirmation dialog explains that in-flight external operations will be
abandoned to unknown state and reconciled later, not force-completed. State persists across restart.
