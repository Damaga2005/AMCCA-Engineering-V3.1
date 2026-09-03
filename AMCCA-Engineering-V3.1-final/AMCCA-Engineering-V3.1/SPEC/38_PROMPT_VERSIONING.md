# 38 — Prompt Versioning

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Model

`prompt_templates` holds the logical prompt; `prompt_versions` holds immutable versions with a body hash
and a body reference. An agent run pins a `prompt_version_id`.

## Immutability

A prompt version is never edited. A change creates a new version. This is not bureaucracy: without it,
`agent_runs` records a prompt id whose content has since changed, and reproducibility silently becomes
fiction while still appearing to work.

## Pinning

An agent contract pins a prompt version. Advancing it is a contract change with a version bump, which
means it is reviewable and revertible.

## Evaluation before promotion

A new prompt version is evaluated against a held-out set of recorded inputs before it may be pinned in a
contract. The comparison is on deterministic validator pass rate and QA outcomes, not on subjective
reading of sample outputs.

## Secrets and personal data

Prompt bodies never contain credentials. Prompt payloads at runtime are minimised and are not retained
beyond what reproducibility requires and policy permits (`SPEC/51`). An input hash is retained even when
the input itself is not, so that a run can be identified even after its content is collected.
