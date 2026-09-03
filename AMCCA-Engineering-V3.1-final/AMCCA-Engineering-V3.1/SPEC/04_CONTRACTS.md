# 04 — Contracts

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## What a contract is

A contract is a JSON Schema in `SCHEMAS/` plus the rules in this file. Contracts govern every trust
boundary: agent output, tool output, provider response, import payload, configuration, and every
persisted object.

## Universal rules

1. Every contract sets `additionalProperties: false`. An unexpected field is a violation, not a curiosity.
2. Every persisted contract object carries `schema_version` (D-004). No exceptions.
3. Every non-root object carries `production_id` where a production exists. A record that cannot say what
   it belongs to cannot participate in traceability.
4. Identifiers are ULID and are pattern-validated.
5. Timestamps are RFC 3339 with an explicit offset.
6. Money is a decimal string with six fractional digits plus an ISO-4217 currency (D-023).
7. Enumerations are closed. A free-form string where a closed set exists is a defect.
8. Conditional invariants that can be expressed in schema are expressed in schema, not in prose.

Rules 2, 3 and 7 each close a specific V2 defect: six of nine schemas lacked `schema_version`,
no schema could link a QA result to its production, and `production.status` was an unconstrained string
in the one place where the state machine most needed enforcement.

## Contract inventory

| Schema | Aggregate | Notable enforced invariant |
|---|---|---|
| `production` | Production | `state` enum generated from `state-machine.json` |
| `publication` | Publication target | `VERIFIED` requires `external_id`, `evidence_source`, `evidence_retrieved_at` (I-11) |
| `job` | Job | `LEASED`/`RUNNING` requires `lease_owner` and `lease_until` (I-05) |
| `event` | Event | `correlation_id` required, `causation_id` present, `transition_id` for state changes (D-018) |
| `audit` | Audit record | `actor_type` has no `AGENT` value (I-09) |
| `agent-run` | Agent invocation | Reproducibility tuple required |
| `tool-run` | Tool invocation | `EXTERNAL_UNSAFE` requires `intent_id` and `idempotency_key` (I-03) |
| `qa` | QA report | Fixed critical dimensions; `check_kind` discriminator (I-19) |
| `claim` | Research claim | At least one source, each with `retrieved_at` and `trust_tier` |
| `rights` | Asset rights | `GREEN` unreachable with unknown commercial or modification terms |
| `cost-event` | Cost | Reservation and settlement are distinct kinds |
| `analytics` | Observation | `provenance` mandatory (I-12) |
| `referral` | Referral link | `ACTIVE` unreachable via `HTTP_CHECK` alone |
| `manifest` | Artifact manifest | Hash and DAG edges per artifact |
| `config` | Configuration | Startup gate |

## Versioning

See `SPEC/58`. Additive optional fields are minor. Removing a field, narrowing an enum, or adding a
required field is major and needs a migration plus a compatibility window.

## Validation placement

Validation runs at the boundary, once, before the object is trusted — never sprinkled through the domain.
A domain object that exists has already been validated; a domain method that re-checks its own inputs is
a sign that a boundary is missing.
