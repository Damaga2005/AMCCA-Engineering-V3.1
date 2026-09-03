# Copyright and Rights Policy

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## The rule

No asset enters a render without a `rights_records` row. `GREEN` is required for the autonomous path;
`YELLOW` requires approval in `MANUAL` and `ASSISTED` and is blocked in `AUTONOMOUS`; `RED` is blocked
everywhere.

`CHECK(status <> 'GREEN' OR (commercial_use = 'ALLOWED' AND modification <> 'UNKNOWN'))` means `GREEN`
cannot be asserted over an unknown licence. The database refuses it.

## What is never treated as permission

- Similarity to a licensed work.
- Availability on the public internet.
- Absence of a visible copyright notice.
- The fact that a model generated the asset. A generated asset may still reproduce protected expression,
  and the generator's terms may impose their own conditions.
- A previous production having used the same asset. Rights expire.

## Attribution

Where attribution is required, the text is stored on the rights record and its presence in the delivered
content is verified at `COMPLIANCE_QA`, not assumed from the record's existence.

## Expiry

A rights record whose `expires_at` has passed degrades to `YELLOW`, which invalidates the assets-ready
guard for any production still depending on it. Retention holds prevent collection of rights evidence
while it is still needed.

## Music and voice

Music licences frequently restrict platform, territory and monetisation independently. These restrictions
are recorded as structured fields, not free text, so preflight can evaluate them against the actual
target. A synthetic voice modelled on an identifiable person is treated as a `PERSON`-class depiction
under `SPEC/45` rule 2.
