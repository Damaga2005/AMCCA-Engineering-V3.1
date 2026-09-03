# 62 — UI State Management

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Principles

1. View models hold presentation state only. Domain state lives in the database.
2. The UI never caches a decision. Whether an action is permitted is asked at the moment of the action,
   not derived from what was permitted when the screen loaded.
3. Updates are event-driven. The UI subscribes to domain events rather than polling, except where a
   metric genuinely requires periodic refresh.
4. Every list is virtualised and paged. This system accumulates hundreds of thousands of events.

Principle 2 prevents a class of bug where a screen loaded before a budget threshold was crossed still
offers an action that will now be refused. The button should be gone or the action should fail cleanly
with a reason, and the second is achieved by re-asking.

## Optimistic updates

Not used for anything that mutates protected state. The UI shows the request as in-flight and updates on
the resulting event. Showing a publication as published before the platform confirms it would be exactly
the false-success this system is built to avoid, reproduced in the interface layer.

## Error surfacing

Every error reaching the UI carries a code from `SPEC/05`, a human message and an operator action.
The code is copyable. A support conversation about this system is conducted in codes.

## Long operations

Progress is derived from real job state, not from a timer. An operation whose progress cannot be
determined shows an indeterminate state honestly rather than a fabricated percentage.

## Offline and degraded

When a provider circuit is open or the network is unavailable, the UI states which capabilities are
degraded and which still work. It does not present a generic connection error over the whole application
when only one adapter is affected.
