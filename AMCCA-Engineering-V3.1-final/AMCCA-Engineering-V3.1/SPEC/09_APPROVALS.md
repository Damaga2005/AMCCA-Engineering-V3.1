# 09 — Approvals

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Properties

An approval is scoped, time-bounded, identity-attributed, auditable and single-use by default.
`approvals` carries `scope_json`, `expires_at`, `single_use`, `decided_by`, `decided_at`, `consumed_at`.

There is no representation for a blanket or standing approval. Adding one would require a schema change,
an ADR and a deliberate decision to weaken the model — which is exactly the friction it should have.

## Lifecycle

`PENDING -> APPROVED -> CONSUMED`, or `PENDING -> REJECTED`, or `PENDING -> EXPIRED`.
Consumption is atomic with the action it authorises: the same transaction that performs the protected
action sets `consumed_at`. A crash between approval and action leaves the approval unconsumed and still
valid until expiry; it never leaves an action half-authorised.

## Scope matching

An approval authorises exactly the action, subject and cost ceiling recorded in `scope_json`. A request
that differs in any of those three is a different request and requires its own approval. Near-matches are
rejected, not accepted with a warning, because "close enough" is how an approval to publish one video
becomes an approval to publish a series.

## Expiry

`expires_at` is mandatory. The default window is short and configurable. An expired approval cannot be
revived; a new one is requested. This bounds the damage from an approval granted and then forgotten.

## What requires approval

Determined by the autonomy matrix in `BLUEPRINT/05`. In `ASSISTED` mode: script generation, media
generation, publication, YELLOW-rights use, new affiliate programs, policy changes and capability
enablement. In `AUTONOMOUS` mode the first three become automatic within budget; the rest remain blocked
outright rather than becoming approvable, because an autonomous system requesting permission to change
its own security policy is a design error.
