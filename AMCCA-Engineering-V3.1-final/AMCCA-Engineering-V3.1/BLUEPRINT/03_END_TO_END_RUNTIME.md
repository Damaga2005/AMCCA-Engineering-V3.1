# End-to-End Runtime

The numbered flow below is the normal path. Every step has a defined failure branch in `SPEC/13`.

1. **Discovery.** Scheduler enqueues a discovery job. Research sources are fetched under SSRF and size
   limits. Signals are stored with `retrieved_at` and a content hash.
2. **Trend and niche.** Signals aggregate into trends, each traceable to a `sources` row. A trend with no
   source cannot exist (D-014).
3. **Opportunity scoring.** Deterministic scoring produces expected revenue, expected cost and a risk
   penalty. These are labelled estimates and cannot reach the revenue ledger (D-030).
4. **Strategy.** An opportunity is selected. `CONCEPT_SELECTED` records the decision and its rationale.
5. **Research.** The research agent proposes claims; the engine validates source count, independence and
   recency, and sets claim status. AI confidence never sets `VERIFIED`.
6. **Script.** Script agent proposes; deterministic validation checks schema, maps every material factual
   line to a `VERIFIED` claim, and applies content policy.
7. **Storyboard.** Scene plan generated and structurally validated against the script.
8. **Assets.** Visual assets generated or sourced. Every asset gets a `rights_records` row. Duplicate
   detection runs. Anything not `GREEN` blocks the autonomous path.
9. **Audio.** Voice and music generated; loudness, clipping and duration checked deterministically.
10. **Edit.** MediaWorker renders a candidate. Output is hashed and entered into the artifact DAG.
11. **QA.** Six stages in order: technical, visual, audio, content, retention, compliance. Deterministic
    checks decide; AI checks contribute evidence.
12. **Scoring.** Aggregate scores compared to thresholds. Pass leads to `FINAL_VERIFIED` and a sealed manifest.
13. **Publication preflight.** Rights, disclosure, synthetic label, platform capability, credential validity,
    metadata, referral validity, budget and kill switch. Any failure blocks.
14. **Publish.** Publication lock acquired; intents persisted and committed; adapters called; request
    identifiers captured.
15. **Verify.** Authoritative platform status polled. Only authoritative evidence produces `VERIFIED`.
16. **Measure.** Analytics ingested with provenance. Attribution chains built. Confirmed revenue recorded.
17. **Learn.** Memory and genome updated from measured outcomes only.

## Where this flow can pause

At every gate, and the pause is always visible: a `policy_decisions` row, a `notifications` row, and a
production in `BLOCKED` with `blocked_from` set. There is no silent stall. If the operator cannot see why
something stopped, that is a defect, not a UX preference.

## Where this flow can become ambiguous

Steps 5, 8, 9, 10, 11 and 14 issue external calls. Any of them can produce
`UNKNOWN_EXTERNAL_STATE`, and only the reconciliation service can resolve it. Step 14 is the expensive
one: an unresolved ambiguity there is the difference between one published video and two.
