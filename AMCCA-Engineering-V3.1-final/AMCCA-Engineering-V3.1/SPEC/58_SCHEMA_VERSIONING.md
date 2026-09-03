# 58 — Schema and Contract Versioning

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Scheme

Semantic versioning per contract. `schema_version` is required on every persisted contract object (D-004)
and is a `const` in each schema, so a document carrying a different version fails validation immediately
rather than being partially interpreted.

| Change | Bump |
|---|---|
| Add an optional field | Minor |
| Add an enum value | Minor if consumers tolerate unknown values, else major |
| Add a required field | **Major** |
| Remove or rename a field | **Major** |
| Narrow a type, pattern or enum | **Major** |
| Change a semantic meaning without changing the shape | **Major** |

The last row is the one people get wrong. A field that keeps its name and type but changes meaning is the
most dangerous possible change, because nothing detects it.

## Migration

A major bump requires: a database migration, an upcast function from the previous major version, a
compatibility window during which both are readable, and a test that round-trips real recorded data.

## Compatibility window

Readers accept the current major and the immediately previous major. Writers emit only the current major.
Beyond that window, data must be migrated forward before it can be read.

## Package version

`PACKAGE_VERSION` in `README.md` is the specification version. It bumps when any contract, decision or
generated artifact changes. `MANIFEST.md` records the hashes that correspond to it.

## Validation

`TOOLS/validate_package.py` asserts that every schema carries `schema_version`, that the value matches the
package version, and that the production state enum matches `state-machine.json`. Six of nine V2 schemas
were missing `schema_version` in direct violation of D-004; this check is why that cannot recur.
