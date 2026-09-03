# 68 — Time and Clock

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Representation

All timestamps are RFC 3339 with an explicit UTC offset, stored as TEXT. SQLite has no date type, so this
is a discipline the application must maintain; a column storing a local time without an offset is a defect.

## The clock is a port

`IClock` is injected everywhere. Nothing calls `DateTime.Now` directly. This is not purism: lease expiry,
retention, budget windows, capability staleness and approval expiry are all clock-dependent, and none of
them can be tested deterministically against a real clock.

## Clock skew

Preflight check 9 compares the system clock to the most recent recorded event timestamp. A clock that has
moved backwards is a warning, because it can:

- expire leases early, allowing two workers to believe they own one job;
- reopen a budget window that was already consumed;
- make a capability verification look fresh when it is stale;
- revive an expired approval.

The lease mechanism defends against the first case with fence tokens. The others are surfaced to the
operator rather than silently absorbed.

## Monotonic time

Durations, timeouts and heartbeat intervals use a monotonic source, never wall-clock differences.
Wall-clock time is for recording when something happened; monotonic time is for measuring how long it
took. Confusing the two produces timeouts that fire at daylight-saving transitions.

## Time zones

Budget windows and schedules are evaluated in the operator's configured local timezone, recorded
explicitly in configuration. A daily budget window whose timezone is ambiguous will reset at a different
moment than the operator expects, twice a year.
