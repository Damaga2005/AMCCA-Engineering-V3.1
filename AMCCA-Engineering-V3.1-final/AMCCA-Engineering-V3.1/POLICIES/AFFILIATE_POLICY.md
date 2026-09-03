# Affiliate Policy

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Explicit configuration only

Referral programs, codes, URLs and commission models are configured by an operator. The system never
discovers, infers, guesses or constructs one. Adding a program is blocked in `AUTONOMOUS` mode.

## Validation

`ACTIVE` requires `OFFICIAL_API`, `OFFICIAL_DASHBOARD`, `OPERATOR_VERIFIED` or `MANUAL_CONFIRMATION`,
plus a recorded `validation_evidence_ref`. **`HTTP_CHECK` alone is never sufficient** and the schema and
the database both refuse it.

A 200 response proves that a URL resolves. It does not prove the code is valid, the program is live, the
commission is as recorded, or that the link is attributed to this account.

## Restrictions

Geographic and platform restrictions are evaluated at preflight against the actual target, not at
configuration time. An expired or restricted link blocks publication with `AMCCA-REF-001`.

## Disclosure

Affiliate disclosure is required wherever the platform or jurisdiction requires it, is determined at
strategy time so it can be designed into the content, and its presence in the delivered content is
verified at `COMPLIANCE_QA`. A missing required disclosure blocks with `AMCCA-CMP-002`.

Disclosure text is versioned per language with a `source_ref`. It is not machine-translated at publish
time, because a disclosure that is grammatically odd in the target language may not satisfy the
requirement it exists to satisfy.
