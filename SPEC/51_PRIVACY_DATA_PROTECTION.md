# 51 — Privacy and Personal Data

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

> **This document did not exist in V2.** The audit found no coverage of personal data anywhere in the
> package. "Disclosure" appeared only in the commercial-affiliate sense.

> **Not legal advice.** This specifies engineering behaviour. Whether a specific processing activity is
> lawful is a question for a qualified adviser in the operator's jurisdiction.

## Regulatory frame

The operator is assumed to be EU-established or to process the data of people in the EU, which brings the
**GDPR** (Regulation (EU) 2016/679) into scope alongside the AI Act transparency duties in `SPEC/45`.
The operator is the **controller**; this system is the tooling through which processing happens.

This file does not attempt to summarise the GDPR, and nothing here is legal advice. It specifies the
mechanisms the system provides so that an operator, advised properly, can meet obligations that fall on
them: minimisation, purpose limitation, retention limits, accuracy, and the ability to locate, export and
erase records about a given person. Whether a specific processing activity is lawful, and on what basis,
is a question for a qualified adviser in the operator's jurisdiction.

## Where personal data enters

| Path | Data | Control |
|---|---|---|
| Research | Names, statements and biographical facts about identifiable people in sources and claims | `claims.contains_personal_data`; minimisation; shorter retention |
| Content | A production may name or depict a real person | Stricter evidence bar; approval required for realistic depiction (`SPEC/45` rule 2) |
| Platform accounts | Operator's own account identifiers | Secret store; not exported |
| Analytics | Aggregate metrics | Aggregates only; no viewer-level data is ingested or stored |
| Logs | Incidental identifiers in error context | Redaction |

The system does not ingest viewer-level analytics even where a platform offers it. Aggregate metrics are
sufficient for every decision the system makes, and the alternative is holding data about people who have
no relationship with the operator.

## Principles applied

1. **Minimisation.** A claim about a person retains the assertion and its source reference, not the full
   retrieved document, once the claim's evidence window closes.
2. **Purpose limitation.** Personal data is used to substantiate a claim in content. It is not used for
   profiling, targeting or model training (D-027).
3. **Shorter retention.** Personal-data-flagged records have their own retention clock in `SPEC/52`,
   shorter than the operational clock.
4. **Export exclusion by default.** Exports and diagnostics bundles exclude personal-data-flagged content
   unless the operator explicitly includes it, which is itself audited.
5. **No training.** Nothing in this system is used to train or fine-tune a model. The provider gateway is
   called for inference only, and the adapter records whether the provider's terms permit that
   expectation.
6. **Accuracy.** A claim about a person that becomes `DISPUTED` invalidates every script asserting it,
   through the DAG, and blocks further publication of affected content.

## Special categories

Claims with `subject_class` of `HEALTH`, `FINANCE` or `LEGAL`, and any claim about an identified private
individual, carry the strictest evidence bar and cannot be asserted autonomously. A realistic synthetic
depiction of an identifiable person requires operator approval in every autonomy mode (`SPEC/45`).

## Operator obligations this system supports

The system provides the mechanisms; the operator remains the controller. Supported: locating every record
referencing a given claim or person by search over `claims` and `sources`; deleting or tombstoning them
with lineage preserved; producing an export of what is held; and recording the basis on which a claim
about a person was published.

What the system cannot do is decide whether a given processing activity is lawful. That decision sits
with the operator, and this file's job is to make sure the operator has the information to make it.
