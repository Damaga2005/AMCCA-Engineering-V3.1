-- AMCCA canonical SQLite DDL (generated artifact).
-- Emitted from `TOOLS/generate_artifacts.py` (schema_version 3.1.0).
-- `--check` diffs this file byte-for-byte against a fresh generation (V31-01).
-- Do not edit by hand; edit build_canonical_ddl() in generate_artifacts.py and run --regen.
--
-- Scope: the load-bearing subset of tables whose CHECK constraints are exercised
-- by TOOLS/test_*.py (V31.1.1 D-DUP-01). This is NOT the full ~40-table catalogue;
-- SPEC/11_DATABASE_SCHEMA.md remains the prose reference for every table.

-- synthetic_declarations
CREATE TABLE synthetic_declarations (
  id TEXT PRIMARY KEY
  -- abbreviated: this test fixture only needs the FK target to exist;
  -- the full column set is documented in SPEC/11 under synthetic_declarations.
);

-- publications
CREATE TABLE publications (
  id TEXT PRIMARY KEY,
  state TEXT NOT NULL,
  external_id TEXT NULL,
  evidence_source TEXT NULL,
  evidence_retrieved_at TEXT NULL,
  synthetic_declaration_id TEXT NULL,
  platform_label_required INTEGER NOT NULL DEFAULT 0,
  synthetic_label_applied INTEGER NOT NULL DEFAULT 0,
  FOREIGN KEY (synthetic_declaration_id) REFERENCES synthetic_declarations(id),
  CHECK (state IN ('INTENT_CREATED', 'UPLOAD_REQUESTED', 'UPLOADED', 'PROCESSING', 'PUBLISHED', 'VERIFIED', 'REJECTED', 'FAILED', 'UNKNOWN_EXTERNAL_STATE', 'CANCELLED')),
  CHECK (platform_label_required = 0 OR synthetic_declaration_id IS NOT NULL),
  CHECK (state <> 'VERIFIED' OR (external_id IS NOT NULL
         AND evidence_source IN ('OFFICIAL_API', 'OFFICIAL_DASHBOARD', 'OPERATOR_CONFIRMATION')
         AND evidence_retrieved_at IS NOT NULL)),
  CHECK (state <> 'VERIFIED' OR platform_label_required = 0 OR synthetic_label_applied = 1)
);

-- platform_capabilities
CREATE TABLE platform_capabilities (
  platform TEXT NOT NULL,
  account_id TEXT NOT NULL,
  capability TEXT NOT NULL,
  status TEXT NOT NULL,
  evidence_source TEXT NOT NULL,
  verified_at TEXT NOT NULL,
  expires_at TEXT NULL,
  PRIMARY KEY (platform, account_id, capability),
  CHECK (status IN ('DISCOVERED', 'VERIFIED', 'UNVERIFIED', 'DISABLED', 'UNSUPPORTED')),
  CHECK (status <> 'VERIFIED' OR evidence_source IN
    ('OFFICIAL_API', 'OFFICIAL_DASHBOARD', 'OFFICIAL_DOCUMENTATION', 'DIRECT_PLATFORM_PROBE', 'OPERATOR_CONFIRMATION'))
);

