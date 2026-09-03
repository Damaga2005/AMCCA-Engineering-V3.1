# 42 — Platform Capability Matrix

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Record

`(platform, account_id, capability)` -> `status`, `evidence_source`, `verified_at`, `expires_at`.

`status` is one of `DISCOVERED`, `VERIFIED`, `UNVERIFIED`, `DISABLED`, `UNSUPPORTED`.

| Status | Meaning | Autonomous use |
|---|---|---|
| `DISCOVERED` | A candidate capability found via a secondary source (blog, agency article, community guide) but not confirmed against the platform itself | Blocked |
| `VERIFIED` | Confirmed by an authoritative source, within the staleness window | Permitted |
| `UNVERIFIED` | Never probed, or probe stale, or probe failed | Blocked |
| `DISABLED` | Operator turned it off | Blocked |
| `UNSUPPORTED` | The platform does not offer it | Blocked; the UI shows it as unavailable, not as failing |

## `DISCOVERED` never becomes `VERIFIED` on its own (V31-09)

A secondary source is useful for finding out that a capability *might* exist. It is never sufficient to
mark it usable. The database enforces this directly:

```
CHECK(status <> 'VERIFIED' OR evidence_source IN
  ('OFFICIAL_API', 'OFFICIAL_DASHBOARD', 'OFFICIAL_DOCUMENTATION',
   'DIRECT_PLATFORM_PROBE', 'OPERATOR_CONFIRMATION'))
```

`evidence_source` values that can only ever sustain `DISCOVERED`: a blog post, an agency article, a
third-party guide, a social post, a forum thread, or community-maintained documentation. None of these
can appear as the `evidence_source` of a `VERIFIED` row — the `CHECK` constraint above rejects it
regardless of what application code intended. See `SPEC/11`, table `platform_capabilities`.

The distinction mirrors, and is drawn from the same principle as, `POLICIES/FACT_CHECKING_POLICY.md`:
knowing a rule exists is not the same as having confirmed it holds. `CONFIG/platforms.yaml` currently
records its platform rules with `verification_status: DISCOVERED` for exactly this reason (V31-09
renamed the earlier `UNVERIFIED_SECONDARY_SOURCE` label to the same `DISCOVERED` vocabulary used
elsewhere in this matrix) — they should be read as `DISCOVERED`, not `VERIFIED`, until checked against
each platform's own documentation.

## Revalidation

Capabilities are revalidated on a configured cadence and immediately after any `AMCCA-PLT-002`
authentication error. An expired row degrades to `UNVERIFIED` automatically. The system does not assume
that a capability verified last month is still available, because platform permissions change with
account standing, policy updates and monetisation status.

## Capability vocabulary

`upload_video`, `upload_short`, `set_thumbnail`, `set_description`, `set_tags`, `schedule_publish`,
`apply_synthetic_label`, `read_metrics`, `read_revenue`, `set_visibility`.

`apply_synthetic_label` is a first-class capability. If a target platform requires a synthetic-content
label and the adapter cannot apply one, the target is not publishable autonomously — see `SPEC/45`.

## Evidence

Every verification records what proved it: an API response, a documented scope grant, or an operator
confirmation. A capability marked `VERIFIED` with no evidence source is an integrity error and is
reported as one.
