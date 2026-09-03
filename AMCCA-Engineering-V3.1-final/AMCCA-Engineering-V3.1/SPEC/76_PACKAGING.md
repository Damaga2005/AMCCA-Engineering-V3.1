# 76 — Packaging

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Output

Self-contained .NET 8 publish for `win-x64`, producing `AMCCA.exe` with its runtime, plus a WiX MSI
installer `AMCCA-Setup.exe`.

## Build reproducibility

Deterministic build flags enabled. The build records: source commit, package version, dependency lock
hash, .NET SDK version and build timestamp. Two builds from the same commit and lock file produce
identical binaries except for the timestamp, and the release process verifies that.

## Contents

`AMCCA.exe` and runtime, default configuration, JSON schemas, migration scripts, policy defaults, licence
files for every dependency, and a release manifest with a SHA-256 for every shipped file.

FFmpeg is **not** bundled. It is an external dependency detected at preflight, with its version checked
against a supported range. Bundling it would create a licensing and update-responsibility problem the
product does not need.

## Signing

Production releases are Authenticode signed. An unsigned build is a development build and identifies
itself as one in the UI and in the diagnostics bundle.

## Release manifest

Version, commit, build identifier, per-file SHA-256, dependency list with versions and licences, and the
package specification version and manifest hash. The specification the binary was built against is
identifiable from the binary, which is what makes a defect report actionable.
