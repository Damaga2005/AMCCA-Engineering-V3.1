# 59 — Dependency Policy

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Pinning

Exact versions in a lock file. No floating ranges, no wildcards. A build that resolves a different
dependency tree than the last build is not reproducible, and an autonomous system that spends money
deserves a reproducible build.

## Adding a dependency

Requires: a stated need that cannot reasonably be met in-box, a licence review, a maintenance check
(release cadence, open critical issues), a transitive-dependency review, and an ADR entry if it
introduces a new capability class or any required external service.

A dependency that requires a network service at runtime is refused by default under D-001.

## Vulnerability scanning

Runs in CI on every build. A production build fails on any advisory at or above the configured severity.
The threshold is a configuration value, recorded, not a judgement made per incident.

## Update cadence

Batched, scheduled, with a changelog entry and a full test run. Security patches may be taken
out-of-band. A batch that fails the test suite is not merged with a note; it is investigated.

## Licence policy

Permissive licences are acceptable. Copyleft licences that would impose obligations on the distributed
binary require explicit review and an ADR. Every dependency's licence is recorded in the release manifest,
because "we do not know what is in our binary" is not a defensible answer.

## FFmpeg

FFmpeg is an external runtime dependency, not a linked library. Its version is checked at preflight
against a supported range and recorded with every render, so an output hash difference across versions is
explicable rather than alarming.
