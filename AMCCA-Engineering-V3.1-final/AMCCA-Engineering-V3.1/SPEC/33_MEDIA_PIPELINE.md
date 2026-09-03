# 33 — Media Pipeline

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Stages

Asset generation or sourcing, voice synthesis, music selection, caption generation, composition, render,
probe, thumbnail.

## MediaWorker rules

1. FFmpeg is invoked through `ProcessStartInfo` with an **argument list**. String concatenation into a
   shell is forbidden (D-008) and covered by a security test.
2. Every invocation has a timeout, an output size ceiling and a working directory confined beneath
   `data_root`.
3. Input paths are canonicalised and validated before invocation.
4. Exit code, stderr tail and duration are recorded. A non-zero exit is `AMCCA-MED-001`; a timeout or
   ceiling breach is `AMCCA-MED-002`.
5. The output is hashed and entered as an artifact version before anything downstream may reference it.

## Determinism

Render parameters are recorded so a render can be reproduced. Where FFmpeg output is not bit-reproducible
across versions, the FFmpeg version is recorded with the artifact so a hash difference can be explained
rather than treated as corruption.

## Asset acquisition

Every asset — generated or sourced — gets a `rights_records` row before it can be used (`SPEC/36`).
Generated assets record `generator_model_id`, which feeds the synthetic-content declaration in `SPEC/45`.
An asset without a rights record cannot enter a render, enforced by the `ASSET_GENERATION -> ASSETS_READY`
guard.

## Resource control

Renders are dispatched only when free space exceeds `minimum_free_gb` plus the estimated output. Concurrent
renders are capped independently of the general worker cap, because FFmpeg is CPU- and IO-hungry and an
unbounded pool starves everything else including the UI.

## Failure

A failed render is retryable within bounds. A repeated identical failure signature stops the retry loop and
routes to rework, because the same command failing the same way is not a transient condition.
