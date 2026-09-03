# 17 — Scheduler

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Responsibilities

Decide what work is eligible, in what order, and whether it can be dispatched now. The scheduler enqueues;
it never executes and never approves.

## Eligibility

A job is dispatchable only if all of the following hold:

1. The kill switch permits it (`NORMAL`, or `PAUSED` only for P0 control work).
2. The policy engine returns `ALLOW` for the underlying action.
3. Required budget is reservable now.
4. Free disk space exceeds `storage.minimum_free_gb` plus the job's estimated output.
5. The provider or platform rate class has capacity.
6. Required capabilities are `VERIFIED` and unexpired.
7. In `ASSISTED` mode, any required approval exists, is unexpired and unconsumed.

Condition 4 exists because a render that runs out of disk halfway leaves a partial artifact, a consumed
budget and a confused state machine. Refusing to start is cheaper than recovering.

## Ordering

Priority class first, then aging, then scheduled time, then creation order. Aging is bounded so a P5 job
cannot preempt P1 publication work no matter how long it waits.

## Autonomous scheduling

In `AUTONOMOUS` mode the scheduler may originate production cycles on a configured cadence, within the
daily and monthly budget windows and only while every gate in the eligibility list holds. It re-evaluates
eligibility at dispatch, not only at enqueue, because a budget or capability can lapse in between.

## Backpressure

When reconciliation backlog, dead-letter count or provider error rate crosses configured thresholds, the
scheduler stops originating new production cycles while continuing to run P0 and P1 work. Producing more
work while unable to resolve existing ambiguity is how a small incident becomes a large one.
