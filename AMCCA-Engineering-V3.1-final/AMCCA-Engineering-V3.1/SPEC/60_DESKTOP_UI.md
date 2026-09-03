# 60 — Desktop Control Center

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Architecture

WPF with MVVM. Views bind to view models; view models call typed application commands. The UI thread
performs no I/O, no database access and no waiting. A frozen UI during a render is a defect, not a
characteristic of desktop applications.

## Screens

Dashboard, Productions, Production Inspector, Job Queue, Approvals, Publications, Money, Evidence,
Policies, Providers, Security, Safety, Settings, Diagnostics.

## Interface obligations

These are normative, not stylistic:

1. The kill switch is reachable in one action from every screen.
2. Autonomy mode and publishing state are visible on every screen, not buried in settings.
3. **Every number carries its provenance.** Measured and estimated values are visually distinct and are
   never presented in the same aggregate without stating the composition.
4. Every blocked item states which rule blocked it, which policy version that rule came from, and what
   would unblock it.
5. Every approval request shows the exact action, subject, cost ceiling and expiry being approved.
6. No screen shows "something went wrong" as a terminal state. Every failure surfaces an error code from
   `SPEC/05` and its operator action.
7. Long-running operations show progress and are cancellable.

Obligation 3 is the one that prevents the most expensive operator error available in this system:
mistaking a forecast for a measurement and making a business decision on it.

## Production Inspector

The most important screen. It shows the full lineage of one production: opportunity and its score
breakdown, claims with sources and retrieval timestamps, the artifact DAG with version states, every QA
finding with its responsible node, every policy decision, every cost event, every publication with its
evidence, and the complete state transition history with transition ids.

If a question about a production cannot be answered from this screen, the evidence plane has a gap.

## Accessibility and localisation

Keyboard navigation throughout, screen-reader labels on controls, and no meaning conveyed by colour
alone — the measured-versus-estimated distinction in obligation 3 uses a shape or label, not just a hue.
Operator strings are resourced (`SPEC/39`); error codes are never localised.
