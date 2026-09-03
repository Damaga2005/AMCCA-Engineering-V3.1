# V3.1.1 version / stale-reference audit

Scope: every occurrence of `3.0.0`, `UNVERIFIED_SECONDARY_SOURCE`, and `PUBLIC_URL_CHECK`
in the repository, classified NORMATIVE / HISTORICAL / EXAMPLE / MIGRATION, fixed at the
source generator (never hand-patched in generated JSON) where NORMATIVE and stale.

## `3.0.0`

| Location | Classification | Action |
|---|---|---|
| `SCHEMAS/state-machine.json` `$id` | **NORMATIVE, stale** | **Fixed at source**: `generate_artifacts.py`'s `build_state_machine_json()` hardcoded the literal `"amcca://data/state-machine/3.0.0"`. Changed to `f"amcca://data/state-machine/{SV}"`, matching every other `$id` in the package. This was the specific defect the external audit flagged; see Phase 1 commit. |
| `README.md` — "Supersedes: ... AMCCA Engineering V3.0.0" | HISTORICAL | Left as-is. Names the predecessor package being superseded; that is the line's entire purpose. |
| `CHANGELOG_V3_TO_V3.1.md` (title, "Basis:", changelog body) | HISTORICAL | Left as-is. The file's whole subject is the V3.0.0 → V3.1.0 transition. |
| `CHANGELOG_V2_TO_V3.md` — "Package version: 3.0.0" | HISTORICAL | Left as-is. Documents what the package version *was* at that point in the changelog's own timeline. |
| `AUDIT/V2_DEFECTS_CLOSED.md` — "a second, independent audit of V3.0.0 itself" | HISTORICAL | Left as-is. Narrates a past audit of the predecessor package by name. |

No other normative site (`schema_version` const/field in any `SCHEMAS/*.json`, any
`CONFIG/*.yaml`) carried a stale version; all already read `3.1.0`.
`TOOLS/test_version_consistency.py` now enforces this mechanically going forward, with the
allowlist documented above encoded as its `HISTORICAL_FILE_PATTERNS` / heading-scoped rule.

## `UNVERIFIED_SECONDARY_SOURCE`

This was the pre-V31-09 name for the `verification_status` value now called `DISCOVERED`
(the rename that made `platform_capabilities`'s "a secondary source can never sustain
VERIFIED" rule use one consistent vocabulary — see V31-09 and `SPEC/11`/`SPEC/42`).

| Location | Classification | Action |
|---|---|---|
| `SPEC/42_PLATFORM_CAPABILITY_MATRIX.md` | **NORMATIVE, stale** | **Fixed.** The prose asserted, in the present tense, that `CONFIG/platforms.yaml` "currently records its platform rules with `verification_status: UNVERIFIED_SECONDARY_SOURCE`" — but `CONFIG/platforms.yaml` was already updated to `verification_status: DISCOVERED` (each entry is commented `# V31-09: secondary source; never sufficient for VERIFIED`). SPEC/42 had not been updated to match, so it described a config file that no longer existed. Corrected to say `DISCOVERED` and note the V31-09 rename explicitly, so a reader isn't sent looking for a value that isn't there. `SPEC/42` is hand-authored prose, not a generated artifact, so this was a direct edit, not a generator change. |
| `AUDIT/V2_DEFECTS_CLOSED.md` — "are marked `UNVERIFIED_SECONDARY_SOURCE`" | HISTORICAL | Left as-is. This is the closed V2→V3 audit's dated snapshot (explicitly "retrieved on 2026-09-02") of what the config said *at that point in the V3 audit*, before V31-09 renamed the value. Rewriting a closed audit's factual record to match a later reality would make the audit historically inaccurate; the current, correct state is described in `SPEC/42` and enforced by `CONFIG/platforms.yaml` itself. |

`CONFIG/platforms.yaml` itself never used the string — its three platform entries already
say `verification_status: DISCOVERED`, confirmed by direct inspection.

## `PUBLIC_URL_CHECK`

This was the pre-V31-06 name for the non-authoritative `publication.evidence_source` value
now called `POST_PUBLISH_CHECK` (a resolving URL is not proof of a verified publication).

| Location | Classification | Action |
|---|---|---|
| `TOOLS/generate_artifacts.py` — `# renamed from PUBLIC_URL_CHECK` | EXAMPLE / migration-note comment | Left as-is. A one-line code comment documenting the rename for future readers; not a live value anywhere. |
| `TOOLS/validate_package.py` — `refs.no_leftover_public_url_check` check, and its detail string | EXAMPLE (the check's own implementation and message) | Left as-is. This is the regression guard *for* the rename: it greps the whole package (excluding `AUDIT/`) for a literal `PUBLIC_URL_CHECK` and fails the build if one is found. Removing this string would remove the guard. |
| `TOOLS/conformance_tests.py` — module docstring, "renamed from PUBLIC_URL_CHECK" | EXAMPLE / migration-note comment | Left as-is, same reasoning as the generator comment. |

`refs.no_leftover_public_url_check` (in `TOOLS/validate_package.py`, run by every
`validate_package.py` / `release_gate.py` invocation) confirms mechanically that no
NORMATIVE occurrence of `PUBLIC_URL_CHECK` exists anywhere outside `AUDIT/`. It currently
passes.

## Summary

One real defect found and fixed at the source generator (`state-machine.json` `$id`,
Phase 1), one real documentation drift found and fixed by direct edit of hand-authored
prose (`SPEC/42`'s stale description of `CONFIG/platforms.yaml`). Every other occurrence
of the three search strings is legitimate historical narration, a migration-note comment,
or the implementation of the regression guard for the corresponding rename — none of it was
mass-deleted, and each is explained above rather than silently removed.
