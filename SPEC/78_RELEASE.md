# 78 — Release Process

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Gates, in order

1. `TOOLS/validate_package.py` passes.
2. `TOOLS/conformance_tests.py` passes.
3. Full test suite green, including `SPEC/72`, `SPEC/73`, `SPEC/74`, `SPEC/75`.
4. Dependency advisory scan below the configured threshold.
5. Migration forward and rollback tests green.
6. Packaging tests green: clean install, upgrade from the previous release, uninstall-preserve, restore.
7. Every criterion in `SPEC/79` demonstrated **by execution**, not asserted in prose (D-029).
8. Release manifest generated and signed.

## Versioning

Application and specification versions are recorded together. A release states which package version it
implements and which manifest hash that package had.

## Changelog

Every release records: contracts changed with version bumps, migrations added, decisions added or amended,
defects closed with their identifiers, and known limitations. Known limitations are stated, not omitted.

## No self-certification

D-029 applies to the release process itself. A release is not approved by a document asserting that the
gates passed; it is approved by the machine-readable outputs of the gate checks being present and green.
The V2 package failed exactly here: its final audit declared readiness that its own artifacts contradicted.

## Rollback

A release that fails in the field is rolled back by reinstalling the previous version and restoring the
`PRE_MIGRATION` backup. Because downgrade migrations are not supported, the backup is the rollback path,
and it is verified before every upgrade for that reason.
