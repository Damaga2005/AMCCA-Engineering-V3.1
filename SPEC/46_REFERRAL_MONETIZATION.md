# 46 — Referral and Monetisation

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Programs and links

A referral program is explicitly configured by the operator. Adding one is `blocked` in `AUTONOMOUS` mode
and requires approval otherwise. The system never discovers, invents or infers a program, a code, a URL or
a commission rate.

## Validation

| Method | Sufficient for `ACTIVE`? |
|---|---|
| `OFFICIAL_API` | Yes |
| `OFFICIAL_DASHBOARD` | Yes |
| `OPERATOR_VERIFIED` | Yes |
| `MANUAL_CONFIRMATION` | Yes |
| `HTTP_CHECK` | **No** |

A 200 response proves a URL resolves. It does not prove the code is valid, the program is live, the
commission is as recorded, or that the link is attributed to this account. `referral.schema.json` carries
a conditional that makes `ACTIVE` unreachable via `HTTP_CHECK` alone, and the database carries the
matching `CHECK`.

## Expiry and restrictions

Every link carries `expires_at`, geographic restrictions and platform restrictions. An expired,
restricted-in-target-geography or uncertain link blocks autonomous publication with `AMCCA-REF-001`.
Restrictions are checked at preflight against the actual target, not at configuration time.

## Disclosure

`DisclosureEngine` determines whether affiliate disclosure is required and where it must appear for each
platform and language. A missing required disclosure blocks publication with `AMCCA-CMP-002`.
Disclosure text is versioned per language with a `source_ref` (`SPEC/39`), not machine-translated at
publish time.

## Money model

| Concept | Table | Constraint |
|---|---|---|
| Expected revenue | `opportunities.expected_revenue` | An estimate; never enters the ledger |
| Attribution signal | `attribution_events` | Carries provenance |
| Revenue | `revenue_events` | `CHECK` forbids `ESTIMATED` provenance (I-13) |
| Cost | `cost_events` | Only `SETTLEMENT` kind counts against profit |

`profit = confirmed revenue - settled attributable cost`. Pending revenue is shown separately and clearly
labelled. A reversal creates a `REVERSED` row rather than editing history.
