#!/usr/bin/env python3
"""
V3.1 P2 hardening (reviewer items 1-3): repository integrity.

The canonical "is this repo clean" test. One coherent check, three clearly
separated sub-checks, each closing a distinct gap none of the existing scripts
covers:

  1. hygiene.no_tracked_junk
     Nothing `git ls-files` reports as tracked matches a junk/cache pattern
     (`__pycache__/`, `*.pyc`, `.pytest_cache/`, etc). This is a question about
     what git actually has committed, which `TOOLS/validate_package.py`'s
     `walk_files()` cannot answer -- it walks the filesystem (skipping a small
     hardcoded dir list) and says nothing about what git tracks vs. what merely
     happens to sit untracked-but-present on disk (e.g. a local `__pycache__/`,
     correctly gitignored and correctly walked around, but never actually
     verified absent from the commit).

     The junk patterns are read from `.gitignore`, not hand-duplicated here --
     `.gitignore` is the single place these patterns are declared. This test
     translates each line into a match rule (see `_pattern_matches()`); it is
     not a full gitignore-semantics engine (no negation, no `**`), which is
     sufficient for this repository's flat, simple pattern list and is
     documented here rather than silently assumed.

  2. hygiene.no_stray_canonical_duplicates
     For every `TOOLS/*.py` file (the canonical location for this package's
     generator and test scripts), no OTHER tracked path anywhere in the repo
     shares its basename. This is a permanent regression test for the earlier
     reviewer claim "a duplicate generate_artifacts.py exists at the repo
     root" -- independently re-confirmed false by a one-off manual
     `git ls-files | grep` in the V3.1.2 pass (see IMPLEMENTATION_SUMMARY.md),
     but a one-off manual check proves nothing about tomorrow. This makes it a
     standing guarantee instead.

  3. hygiene.manifest_entries_are_git_tracked
     Every path listed in `MANIFEST.sha256` is a real git-tracked file. This
     is the piece `TOOLS/validate_package.py`'s `manifest.matches_tree` cannot
     provide: that check compares the manifest against the SAME filesystem
     walk the manifest was generated from, so it always agrees with itself
     regardless of whether a junk file (e.g. a stray `.pytest_cache/` entry
     that isn't in the hardcoded `skip_dirs`) got walked into the manifest.
     Cross-checking against `git ls-files` instead answers the question that
     actually matters for a release manifest: does every entry correspond to
     something genuinely under version control. (`manifest.matches_tree` and
     `manifest.excludes_itself` themselves are NOT re-implemented here --
     they already exist in `TOOLS/validate_package.py` and are correct; see
     IMPLEMENTATION_SUMMARY.md's P2 hardening section for the evidence.)

  4. hygiene.all_tracked_files_in_manifest
     The bidirectional completion of check 3: every path tracked by git
     (except the self-excluded MANIFEST.md and MANIFEST.sha256) MUST be present
     in `MANIFEST.sha256`. If walk_files() erroneously skips, drops, or conceals
     any legitimate tracked file (e.g. .gitignore), this check immediately fails.

  5. hygiene.worktree_metadata_isolation
     Verifies that walk_files() isolates internal SCM metadata (.git), whether
     .git is a directory (standard clone) or a file (worktree / submodule),
     while strictly preserving valid dotfiles (.gitignore, .github/*) and
     never treating project files with 'git' in their name as SCM metadata.

    python TOOLS/test_repository_hygiene.py
"""
import fnmatch
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(ROOT, "TOOLS")
GITIGNORE = os.path.join(ROOT, ".gitignore")
MANIFEST_SHA = os.path.join(ROOT, "MANIFEST.sha256")


def _git_ls_files():
    r = subprocess.run(["git", "ls-files"], cwd=ROOT, capture_output=True, text=True, check=True)
    return [line for line in r.stdout.splitlines() if line.strip()]


def _load_gitignore_patterns():
    """.gitignore is the single source of truth for junk/cache patterns -- this
    reads it rather than hand-duplicating a second list here (reviewer item 1)."""
    patterns = []
    with open(GITIGNORE, encoding="utf-8") as f:
        for line in f:
            s = line.strip()
            if not s or s.startswith("#"):
                continue
            patterns.append(s)
    return patterns


def _pattern_matches(pattern, relpath):
    """Approximate gitignore matching for one pattern against one repo-relative,
    '/'-separated path. Supports: a trailing '/' meaning "matches this directory
    name at any depth" (e.g. `__pycache__/`, `.venv/`), and a bare glob with no
    '/' meaning "matches this basename, or any path segment, at any depth"
    (e.g. `*.pyc`, `.DS_Store`). Deliberately not a full gitignore engine (no
    negation, no `**`, no anchoring on a leading '/') -- this repo's
    `.gitignore` uses none of those, and this docstring is where that scope
    limit is recorded rather than left implicit."""
    parts = relpath.split("/")
    if pattern.endswith("/"):
        pat = pattern[:-1]
        return any(fnmatch.fnmatch(seg, pat) for seg in parts[:-1])
    return fnmatch.fnmatch(parts[-1], pattern) or any(fnmatch.fnmatch(seg, pattern) for seg in parts)


def check_no_tracked_junk(tracked, patterns):
    offenders = []
    for relpath in tracked:
        for pat in patterns:
            if _pattern_matches(pat, relpath):
                offenders.append((relpath, pat))
                break
    name = "hygiene.no_tracked_junk"
    if offenders:
        print(f"FAIL  {name}")
        for relpath, pat in offenders:
            print(f"        {relpath}  (matches .gitignore pattern {pat!r})")
        return False
    print(f"PASS  {name} ({len(tracked)} tracked files, 0 match any of "
          f"{len(patterns)} .gitignore junk patterns)")
    return True


def check_no_stray_canonical_duplicates(tracked):
    canonical_pys = sorted(fn for fn in os.listdir(TOOLS) if fn.endswith(".py"))
    by_basename = {}
    for relpath in tracked:
        by_basename.setdefault(os.path.basename(relpath), []).append(relpath)

    offenders = []
    for fn in canonical_pys:
        hits = [p for p in by_basename.get(fn, []) if p != f"TOOLS/{fn}"]
        if hits:
            offenders.append((fn, hits))

    name = "hygiene.no_stray_canonical_duplicates"
    if offenders:
        print(f"FAIL  {name}")
        for fn, hits in offenders:
            print(f"        TOOLS/{fn} duplicated at: {hits}")
        return False
    print(f"PASS  {name} ({len(canonical_pys)} canonical TOOLS/*.py files, "
          f"0 stray duplicates elsewhere in the tracked tree)")
    return True


def check_manifest_entries_are_git_tracked(tracked):
    tracked_set = set(tracked)
    listed = []
    with open(MANIFEST_SHA, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line.strip():
                continue
            # format: "<64-hex-sha256>  <relpath>"
            _, _, relpath = line.partition("  ")
            listed.append(relpath)

    orphans = [p for p in listed if p not in tracked_set]
    self_listed = [p for p in listed if p in ("MANIFEST.md", "MANIFEST.sha256")]

    name = "hygiene.manifest_entries_are_git_tracked"
    if orphans or self_listed:
        print(f"FAIL  {name}")
        if orphans:
            print(f"        listed in MANIFEST.sha256 but not git-tracked: {orphans[:10]}")
        if self_listed:
            print(f"        MANIFEST.sha256 lists itself/MANIFEST.md: {self_listed}")
        return False
    print(f"PASS  {name} ({len(listed)} manifest entries, all git-tracked, "
          f"none self-referential)")
    return True


def check_all_tracked_files_in_manifest(tracked):
    """Bidi-integrity check: Every git-tracked file except MANIFEST.md and
    MANIFEST.sha256 MUST appear in MANIFEST.sha256. This guarantees that
    walk_files() cannot silently drop, skip, or conceal any real tracked file."""
    tracked_set = set(tracked) - {"MANIFEST.md", "MANIFEST.sha256"}
    listed = set()
    with open(MANIFEST_SHA, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line.strip():
                continue
            _, _, relpath = line.partition("  ")
            listed.add(relpath)

    missing_from_manifest = sorted(tracked_set - listed)
    name = "hygiene.all_tracked_files_in_manifest"
    if missing_from_manifest:
        print(f"FAIL  {name}")
        print(f"        git-tracked but omitted from MANIFEST.sha256: {missing_from_manifest[:10]}")
        return False
    print(f"PASS  {name} (all {len(tracked_set)} git-tracked content files are present in MANIFEST.sha256)")
    return True


def check_worktree_metadata_isolation():
    """Verify that walk_files() strictly and exclusively isolates SCM metadata (.git),
    whether .git is a directory (standard clone) or a file (worktree / submodule),
    and NEVER drops or ignores any valid project dotfile (.gitignore, .github/*)
    or file with 'git' in its name."""
    sys.path.insert(0, TOOLS)
    import validate_package as vp
    walked = set(vp.walk_files())
    name = "hygiene.worktree_metadata_isolation"

    # 1. .git must never be in walked files
    leaked = [p for p in walked if p == ".git" or p.endswith("/.git")]
    if leaked:
        print(f"FAIL  {name} (.git leaked into walk_files(): {leaked})")
        return False

    # 2. .gitignore MUST be in walked files
    if ".gitignore" not in walked:
        print(f"FAIL  {name} (.gitignore was erroneously excluded by walk_files())")
        return False

    # 3. .github workflow files MUST be in walked files
    github_files = [p for p in walked if p.startswith(".github/")]
    if not github_files:
        print(f"FAIL  {name} (.github/ directory files were erroneously excluded)")
        return False

    # 4. If .git is a worktree file on disk, verify it is truly a git worktree pointer
    git_path = os.path.join(ROOT, ".git")
    if os.path.isfile(git_path):
        with open(git_path, "r", encoding="utf-8") as f:
            header = f.readline().strip()
        if not header.startswith("gitdir:"):
            print(f"FAIL  {name} (.git is a file but not a valid gitdir pointer: {header!r})")
            return False

    print(f"PASS  {name} (walk_files isolates .git without excluding .gitignore, .github, or project files)")
    return True


def run():
    tracked = _git_ls_files()
    patterns = _load_gitignore_patterns()

    ok = True
    ok &= check_no_tracked_junk(tracked, patterns)
    ok &= check_no_stray_canonical_duplicates(tracked)
    ok &= check_manifest_entries_are_git_tracked(tracked)
    ok &= check_all_tracked_files_in_manifest(tracked)
    ok &= check_worktree_metadata_isolation()
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(run())
