# 82 — Tool Contracts

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

`SPEC/07` defines the registry. This file lists the tools.

| Tool | Class | Purpose | Notes |
|---|---|---|---|
| `research.search` | `READ` | Query a configured research source | Rate-classed per source |
| `research.fetch` | `READ` | Retrieve a document | Full `SPEC/28` security path |
| `media.probe` | `READ` | Probe a media file | FFprobe via argument list |
| `media.render` | `LOCAL_WRITE` | Render a candidate | Timeout, output ceiling, confined working directory |
| `media.thumbnail` | `LOCAL_WRITE` | Generate a thumbnail | As above |
| `artifact.write` | `LOCAL_WRITE` | Persist an artifact version | Hash computed and verified |
| `gateway.text` | `EXTERNAL_IDEMPOTENT` | Text generation | Idempotency key; request id captured |
| `gateway.image` | `EXTERNAL_UNSAFE` | Image generation | Billable and non-repeatable; intent required |
| `gateway.video` | `EXTERNAL_UNSAFE` | Video generation | As above; async task id persisted before polling |
| `gateway.speech` | `EXTERNAL_UNSAFE` | Speech synthesis | As above |
| `platform.upload` | `EXTERNAL_UNSAFE` | Publish | Intent, lock, unique constraint |
| `platform.status` | `READ` | Authoritative status | Used by reconciliation |
| `platform.list_recent` | `READ` | List recent items | Reconciliation fallback |
| `platform.apply_label` | `EXTERNAL_UNSAFE` | Apply a synthetic-content label | Gate for `SPEC/45` |
| `platform.metrics` | `READ` | Retrieve metrics | Provenance `API_MEASURED` |
| `affiliate.validate` | `READ` | Validate a referral | `HTTP_CHECK` result cannot sustain `ACTIVE` |

## Why generation is `EXTERNAL_UNSAFE`

`gateway.image`, `gateway.video` and `gateway.speech` are billable and not reliably deduplicated by the
provider. A repeat is a real cost with no recovered value, so each requires a committed intent and none
may be blindly retried after an ambiguous failure. `gateway.text` is classified one level lower only
because its cost per call is small enough that bounded retry is defensible — and even it carries an
idempotency key.

## Registration rule

A tool with no declared side-effect class cannot be registered. A tool whose class is raised in a new
version is a major version change requiring every agent contract that grants it to be re-approved.
