# 57 — Export and Import

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Export package

A production export contains: the production record, its artifact manifest, the referenced artifact files,
its claims and sources, its QA reports, its rights records, its publication records with evidence, its
cost events, and a package manifest with a hash for every file.

Excluded by default: secrets in any form, personal-data-flagged claims and sources, raw provider payloads,
and logs. Including personal-data-flagged content requires an explicit operator choice, which is audited.

## Integrity

Every file hash is verified against the artifact manifest before packaging. A mismatch aborts the export
rather than producing a package that quietly disagrees with itself.

## Import

1. Validate the package manifest schema.
2. Verify every file hash before accepting anything.
3. Validate archive safety: entry count, uncompressed size, per-entry path (`AMCCA-SEC-004`).
4. Check `schema_version` compatibility per `SPEC/58`; refuse a newer major version.
5. Import into a staging namespace; do not merge into live records until validation completes.
6. Assign new local identifiers; the imported production references its origin rather than claiming it.

Step 6 matters: an imported production is a copy, not the original, and its publication evidence belongs
to the system that made it. Treating it otherwise would let an import assert publications this system
never made.

## What import never does

Import never restores credentials, never marks an imported publication as `VERIFIED` on the strength of the
package's own claim, and never re-enables a capability. Verification evidence is not transferable.
