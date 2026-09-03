# 69 — Diagnostics and Support Bundle

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Purpose

Produce a package sufficient to diagnose a problem without exposing anything that should not leave the
machine.

## Contents

Application version and build identifier, package version and manifest hash, configuration **with all
secret references shown as references and never resolved**, migration state, preflight results, recent
redacted logs, job queue summary, dead-letter list with error codes, provider circuit states,
reconciliation backlog, storage summary, and the last N events for the affected production with payloads
redacted.

## Exclusions, enforced not requested

Secrets and resolved secret values, tokens, full provider request or response bodies, full retrieved
source documents, artifact media files, and personal-data-flagged claims and sources.

Exclusion is implemented as an allow-list of what may be included, not a deny-list of what must be
removed. A deny-list fails silently the first time a new field is added.

## Verification

The bundle generator runs the same redaction middleware as the logging pipeline, and a security test
generates a bundle from a database seeded with known secret markers and asserts none appear
(`SPEC/72`). A bundle that has not been through that test is not shipped.

## Operator control

The operator sees the file list and can inspect the bundle before sharing it. Nothing is transmitted
automatically; there is no telemetry endpoint in this product.
