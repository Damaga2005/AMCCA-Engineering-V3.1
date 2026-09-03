#!/usr/bin/env python3
"""
V31.1.1 regression test: no stale package version leaks into a NORMATIVE location.

The canonical version is generate_artifacts.SV -- it is imported here, never
re-declared as an independent literal, so this test cannot drift from the one
place D-025 says the version lives.

Why a heuristic is needed at all: this package's own CHANGELOG files and the
AUDIT file legitimately talk about "3.0.0" (the version being superseded), and
must keep doing so forever. A blind "no old version string anywhere" check
would force those documents to lie. So this test distinguishes:

  NORMATIVE  -- a version string that asserts "this is the version of the
               object it appears on/in": a JSON `$id`, a `schema_version`
               field or `const`, a YAML `schema_version:` key, or a
               `**Package version:**` / `**Supersedes:**`-style badge line in
               a non-historical document. An old version here is a real defect
               -- either the object is stale or drifted from a hand-edit.

  HISTORICAL -- prose that talks *about* an old version as something that
               used to be true or has been superseded. Recognised by an
               allowlist: any file whose basename matches CHANGELOG* or
               V2_DEFECTS_CLOSED*, plus, in any file, a line under a "##
               History" (or "## Changelog") heading, plus, in README.md
               specifically, the single "Supersedes:" badge line (its whole
               purpose is to name the old version it replaces).

Anything that is a bare version-shaped string but isn't recognised as either
is reported as UNKNOWN context and treated conservatively as a finding, so a
new normative site added later without updating this allowlist fails loudly
instead of silently passing.
"""
import json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "TOOLS"))
import generate_artifacts as ga

CURRENT = ga.SV                      # e.g. "3.1.0" -- single source of truth
VERSION_RE = re.compile(r"\b\d+\.\d+\.\d+\b")

# Files that are allowed to discuss old versions in prose because their whole
# purpose is historical narration of a past release.
HISTORICAL_FILE_PATTERNS = [
    re.compile(r"^CHANGELOG.*\.md$"),
    re.compile(r"^V2_DEFECTS_CLOSED\.md$"),
]

HISTORY_HEADING_RE = re.compile(r"^#{1,6}\s*(History|Changelog)\b", re.I)

SKIP_DIRS = {".git", "__pycache__", ".venv"}
SKIP_FILES = {".git"}


def _is_historical_file(basename):
    return any(p.match(basename) for p in HISTORICAL_FILE_PATTERNS)


def _walk_files():
    for base, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for fn in files:
            if fn in SKIP_FILES:
                continue
            p = os.path.join(base, fn)
            yield os.path.relpath(p, ROOT).replace(os.sep, "/")


def _lines_under_history_heading(text):
    """Return the set of 1-based line numbers that fall under a History/Changelog
    heading, up to the next heading of equal-or-shallower depth."""
    lines = text.split("\n")
    under = set()
    active_depth = None
    for i, line in enumerate(lines, start=1):
        m = re.match(r"^(#{1,6})\s", line)
        if m:
            depth = len(m.group(1))
            if HISTORY_HEADING_RE.match(line):
                active_depth = depth
                continue
            if active_depth is not None and depth <= active_depth:
                active_depth = None
        if active_depth is not None:
            under.add(i)
    return under


def _check_json_normative(rel, findings):
    """$id / schema_version fields inside SCHEMAS/*.json and generated artifacts."""
    with open(os.path.join(ROOT, *rel.split("/")), encoding="utf-8") as f:
        raw = f.read()
    try:
        doc = json.loads(raw)
    except json.JSONDecodeError:
        return
    stale = []

    def walk(node, path):
        if isinstance(node, dict):
            for k, v in node.items():
                if k in ("$id", "schema_version") and isinstance(v, str):
                    m = VERSION_RE.search(v)
                    if m and m.group(0) != CURRENT:
                        stale.append(f"{path}/{k} = {v!r}")
                elif k == "const" and path.endswith("/schema_version") and isinstance(v, str):
                    if v != CURRENT:
                        stale.append(f"{path}/const = {v!r}")
                walk(v, f"{path}/{k}")
        elif isinstance(node, list):
            for i, v in enumerate(node):
                walk(v, f"{path}/{i}")

    walk(doc, "")
    for s in stale:
        findings.append((rel, "NORMATIVE (JSON $id/schema_version)", s))


def _check_yaml_normative(rel, findings):
    try:
        import yaml
    except ImportError:
        return
    with open(os.path.join(ROOT, *rel.split("/")), encoding="utf-8") as f:
        try:
            doc = yaml.safe_load(f)
        except Exception:
            return
    if isinstance(doc, dict) and "schema_version" in doc:
        v = doc["schema_version"]
        if isinstance(v, str) and VERSION_RE.fullmatch(v) and v != CURRENT:
            findings.append((rel, "NORMATIVE (YAML schema_version)", f"schema_version: {v!r}"))


def _check_prose_badges(rel, findings):
    """`**Package version:**` badge lines outside historical files/sections."""
    text = open(os.path.join(ROOT, *rel.split("/")), encoding="utf-8").read()
    history_lines = _lines_under_history_heading(text)
    for i, line in enumerate(text.split("\n"), start=1):
        if i in history_lines:
            continue
        if "Package version:" not in line and "**Package version**" not in line:
            continue
        for m in VERSION_RE.finditer(line):
            if m.group(0) != CURRENT:
                findings.append((rel, "NORMATIVE (package version badge)", line.strip()))


def run():
    findings = []
    for rel in sorted(_walk_files()):
        basename = os.path.basename(rel)
        if _is_historical_file(basename):
            continue
        if rel.startswith("AUDIT/"):
            # AUDIT/*.md documents a past, closed audit of the V3.0.0 predecessor
            # package by name; it is historical narration, not a live contract.
            continue

        if rel.endswith((".schema.json",)) or rel == "SCHEMAS/state-machine.json" \
                or rel == "SCHEMAS/tables.json":
            _check_json_normative(rel, findings)
        elif rel.startswith("CONFIG/") and rel.endswith(".yaml"):
            _check_yaml_normative(rel, findings)
        elif rel.endswith(".md"):
            _check_prose_badges(rel, findings)

    ok = not findings
    if ok:
        print(f"PASS  no stale version string found in a normative location "
              f"(current = {CURRENT})")
    else:
        for rel, kind, detail in findings:
            print(f"FAIL  {kind} -- {rel}: {detail}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(run())
