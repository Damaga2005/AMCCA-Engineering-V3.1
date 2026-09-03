# Privacy Policy

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

> The engineering specification is `SPEC/51`. This file states the policy position.
> Not legal advice. The operator is the GDPR controller; the lawfulness of any given processing is a
> question for a qualified adviser in the operator's jurisdiction.

## Position

Personal data enters this system incidentally, through research about people who appear in the subject
matter. It is treated as a tracked class with its own handling, not as ordinary content (D-027).

## Rules

1. **Minimisation.** Once a claim's evidence window closes, the assertion and source reference are
   retained; the full retrieved document is not.
2. **Purpose limitation.** Personal data substantiates a claim in content. It is not used for profiling,
   targeting, or training any model.
3. **No viewer data.** Aggregate platform metrics only. Viewer-level analytics are not ingested even
   where a platform offers them, because the system makes no decision that requires them.
4. **Shorter retention.** Personal-data-flagged records have the shortest operational clock in `SPEC/52`.
5. **Export exclusion by default.** Exports and diagnostics bundles exclude flagged content unless the
   operator explicitly includes it, which is audited.
6. **Accuracy.** A claim about a person that becomes `DISPUTED` invalidates every script asserting it
   through the DAG and blocks further publication of affected content.

## Supported operator actions

Locate every record referencing a person or claim; delete or tombstone with lineage preserved; produce an
export of what is held; and retrieve the recorded basis on which a claim about a person was published.
