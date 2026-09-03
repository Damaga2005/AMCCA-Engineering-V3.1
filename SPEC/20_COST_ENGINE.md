# 20 — Cost and Budget Engine

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Money representation

Decimal string with six fractional digits plus an ISO-4217 currency, `decimal` in code (D-023).
Floating point is forbidden. A budget that drifts by fractions of a cent per operation fails open after
enough operations, and this system is designed to perform many operations unattended.

## Budget windows and precedence

| Window | Scope | Reset |
|---|---|---|
| `PRODUCTION` | One production, all its jobs and rework | Per production |
| `REWORK` | Rework attempts for one production | Per production |
| `RECOVERY` | Reconciliation and recovery work | Per production |
| `DAILY` | All spend in a calendar day, local timezone | Daily |
| `MONTHLY` | All spend in a calendar month | Monthly |

**Precedence:** a reservation must satisfy *every* applicable window. The most restrictive binds.
`PRODUCTION`, `REWORK` and `RECOVERY` are sub-budgets of the same production and are not fungible:
exhausting the rework budget does not permit borrowing from the production budget.

**Consistency rule:** `per_production <= daily <= monthly`, enforced at preflight with `AMCCA-CFG-004`.

> *V2 defect closed:* V2 shipped `daily: 20` against `monthly: 300` with no precedence rule and no
> consistency check, so twenty-eight active days would have breached the monthly cap with no defined
> behaviour.

## Reservation

```sql
UPDATE budgets
   SET reserved = reserved + :amount
 WHERE id = :budget_id
   AND reserved + :amount <= limit_amount;
```

One statement. The limit check is in the `WHERE` clause, so two concurrent workers cannot both pass it
(I-06). The reservation row and a `cost_events` row of kind `RESERVATION` are written in the same
transaction (TX-3). Reservations expire; expiry releases the hold.

## Settlement

On completion, actual cost is computed and a `SETTLEMENT` event written; the unused remainder is released.
Reserved is not spent. Only settled costs enter profit calculations.

## Thresholds

`warn_percent`, `pause_percent`, `block_percent`. WARN notifies. PAUSE stops originating new production
cycles while allowing in-flight work and all P0/P1 work to complete — stopping mid-render wastes what was
already spent. BLOCK refuses new reservations entirely.

## Profit

`profit = sum(revenue_events where state = CONFIRMED) - sum(cost_events where kind = SETTLEMENT)`,
attributed through the publication and referral chain. Expected revenue, expected cost and reserved
amounts appear nowhere in this expression (D-030).
