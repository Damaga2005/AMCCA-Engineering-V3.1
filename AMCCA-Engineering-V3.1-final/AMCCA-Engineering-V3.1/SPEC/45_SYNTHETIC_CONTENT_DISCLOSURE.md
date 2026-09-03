# 45 — Synthetic Content Disclosure

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

> **This document did not exist in V2.** An audit of the V2 package found the terms "AI-generated",
> "synthetic", "watermark", "C2PA" and "AI Act" appeared **zero times** across all 133 files, in a system
> whose entire purpose is publishing AI-generated video. This is the file that closes that gap.

> **Verification status.** The regulatory and platform facts below were retrieved on **2 September 2026**
> from the sources cited. They are inputs with a retrieval date, not settled knowledge. Platform policies
> and regulatory guidance in this area change frequently. **Re-verify against the primary sources before
> production use, and treat this file as stale after the configured window.** This obligation is itself
> normative (`POLICIES/FACT_CHECKING_POLICY.md`).

> **Not legal advice.** This file specifies engineering behaviour. Whether a particular production
> triggers a particular legal duty is a question for a qualified adviser in the operator's jurisdiction.

## Why this is a blocking gate

An unlabelled synthetic publication creates three distinct exposures at once: a regulatory one, a platform
one, and a reputational one. Unlike a QA defect, it cannot be fixed by re-rendering after the fact — the
content is already public. It therefore blocks at preflight, at the same severity as a rights failure.

## Regulatory context (EU)

The operator of this system is assumed to be EU-established or to reach EU viewers.

- EU AI Act Article 50 sets transparency obligations covering, among other cases, AI-generated content
  and deepfakes. **These obligations apply from 2 August 2026** — that is, they are already in force as of
  this package's authoring date. Source: European Commission,
  `https://digital-strategy.ec.europa.eu/en/policies/guidelines-ai-transparency-obligations`, retrieved 2026-09-02.
- The Commission adopted guidelines on Article 50 on 20 July 2026, alongside a Code of Practice on
  Transparency of AI-Generated Content. Same source, same retrieval date.
- The obligations fall on **deployers** as well as providers. An operator publishing AI-generated video is
  a deployer. Source: `https://ai-act-service-desk.ec.europa.eu/en/ai-act/article-50`, retrieved 2026-09-02.
- Article 50(2) requires machine-readable marking of synthetic outputs by providers. Reporting indicates a
  transitional deadline of 2 December 2026 for generative systems already on the market before 2 August 2026.
  Source: `https://artificialintelligenceact.eu/transparency-rules-article-50/`, retrieved 2026-09-02.
  *I am less confident about the precise scope of this transitional provision than about the 2 August date;
  verify it directly if it affects a decision.*
- Reported penalty exposure reaches EUR 15 million or 3% of worldwide annual turnover. Verify current
  figures against the Act itself before relying on them.

## Platform requirements (retrieved 2026-09-02, verify before use)

The three target platforms converge on the same test — **does the content realistically depict people,
voices, places or events** — and diverge on mechanism.

| Platform | Trigger | Mechanism |
|---|---|---|
| YouTube | Realistic altered or synthetic content: a real person appearing to say or do something they did not, altered footage of a real event or place, a realistic depiction of an event that did not occur | Creator-side disclosure in Studio under Attributes; YouTube may apply a label itself; repeated non-disclosure can affect content standing and Partner Program eligibility |
| TikTok | Realistic AI-generated visuals or audio depicting people, places or events | Visible AI-generated label; C2PA Content Credentials auto-detected; synthetic media of private individuals reported as prohibited |
| Instagram / Facebook | Realistic AI-generated imagery | Meta AI-info tag; self-declaration plus metadata detection |

**Consistently exempt across all three:** AI assistance with scripting, captions, subtitles, translation,
colour grading, noise removal, reframing and cropping. The test is about depiction, not about which tools
touched the file.

Sources: `https://frameos.studio/blog/ai-content-disclosure-labels`,
`https://creatorsagency.co/blog/youtube-tiktok-ai-disclosure-rules-2026`,
`https://storrito.com/resources/tiktoks-2026-ai-labeling-rules-and-what-they-signal-for-platform-governance/`,
all retrieved 2026-09-02. These are secondary sources; each platform's own help page is the primary source
and should be consulted before implementation.

## The declaration

Every production produces a `synthetic_declarations` row before `COMPLIANCE_QA`:

| Field | Meaning |
|---|---|
| `generated_components_json` | Which components were AI-generated or materially altered, derived from `artifact_versions.generator_model_id` — computed from lineage, not from an agent's assertion |
| `platform_label_required` | Per target, from the decision rules below |
| `platform_label_applied` | Set only after `IPlatformAdapter.ApplySyntheticLabel` confirms |
| `in_content_disclosure_text` | Visible or spoken disclosure text where used |
| `policy_basis` | Which rule required it, with the policy version |

Deriving `generated_components_json` from the artifact DAG rather than from a declaration is deliberate:
the system knows which assets came from a generator because it recorded `generator_model_id` when it
created them. An agent cannot understate it.

## Responsibility matrix (V31-08)

Not every AI Act or platform obligation is AMCCA's to discharge, and conflating them was a defect:
treating "AI-generated" as automatically meaning "AMCCA must embed C2PA in every case" ignores that some
of these duties fall on the model provider, some on the deployer (the operator, using AMCCA), some on the
platform, and only some are things AMCCA's own internal control can actually verify or enforce.

| Obligation | Who is responsible | What AMCCA's internal control does |
|---|---|---|
| Machine-readable marking of the model's own output (AI Act Art. 50(2)) | **Provider** (the AI model/gateway vendor) | Verify the marking is present when the provider's API surfaces it; record it in `rights_records.generator_model_id` and `synthetic_declarations`. AMCCA does not create this marking itself — it can only check for and propagate what the provider emits. |
| Deepfake / synthetic-content disclosure to the audience (AI Act Art. 50(4)) | **Deployer** (the operator, through AMCCA as their tooling) | **MUST apply.** This is the one AMCCA enforces as a hard gate: `synthetic_declarations`, the `COMPLIANCE_QA` stage, and the structural schema conditional on `publications` (I-18, tightened by V31-07). |
| Platform-native AI-content label (YouTube Attributes, TikTok label, Meta AI-info tag) | **Platform mechanism**, triggered by the deployer's disclosure | AMCCA's adapter layer applies it via `IPlatformAdapter.ApplySyntheticLabel` and records `platform_label_applied`. This is the capability gated in `SPEC/42`. |
| C2PA Content Credentials / provenance | **Technical mechanism**, populated by whichever tool in the chain supports it (often the provider, sometimes the render step) | Register and propagate provenance metadata when available (`SPEC/33`); embed C2PA in the render step where AMCCA's own pipeline can produce it. This is a SHOULD, not a MUST, precisely because AMCCA cannot guarantee every upstream generator supports it. |
| Internal audit trail of what was AI-generated and why a label was or wasn't required | **AMCCA** | **MUST.** `synthetic_declarations.generated_components_json` and `.responsibility_json`, derived from the artifact DAG (`artifact_versions.generator_model_id`), never from an agent's self-report. |

The rule this table exists to enforce: **do not silently convert "AI-generated" into "C2PA is now
mandatory for this asset."** C2PA is a SHOULD, tracked separately from the disclosure MUST. Conflating
the two would make AMCCA either over-block content whose provenance metadata a provider didn't supply
(false positive) or, worse, treat the presence of C2PA as satisfying a disclosure obligation it does not
satisfy (false negative). `synthetic_declarations.responsibility_json` keeps the two questions separate
in storage, not just in this table.

## Decision rules

1. If any visual or audio component has `provenance = GENERATED` **and** the content realistically
   depicts a person, voice, place or event, then `platform_label_required = true` for every target.
2. If the content depicts a real, identifiable person, apply rule 1 and additionally require operator
   approval regardless of autonomy mode. Autonomous generation of realistic depictions of identifiable
   people is not a decision this system makes unattended.
3. Text-only AI assistance — script, captions, translation — does not by itself trigger a platform label.
4. Where a target platform provides a native label mechanism, it MUST be used. A description-only
   disclosure is not a substitute for the platform's own mechanism.
5. Where the declaration requires a label and the target's `apply_synthetic_label` capability is not
   `VERIFIED`, **the target is not publishable autonomously**. It blocks with `AMCCA-CMP-001`.
6. C2PA Content Credentials SHOULD be embedded where the pipeline can produce them, in addition to the
   platform label. They serve the machine-readable marking direction of Article 50(2) and are
   independently useful for provenance.
7. Rules are configured per platform in `CONFIG/platforms.yaml` with `source_ref` and `retrieved_at`.
   A rule set older than the staleness window degrades the platform capability to `UNVERIFIED`.

## Enforcement points

| Point | Check |
|---|---|
| `SPEC/30` strategy | Disclosure requirement determined before generation, so it is designed in |
| `COMPLIANCE_QA` | Declaration exists, is complete and matches the artifact DAG |
| `SPEC/49` preflight | `platform_label_required = true` implies the capability is `VERIFIED` |
| `SPEC/44` step 9 | `platform_label_applied` confirmed before `VERIFIED` |
| Database | `CHECK` on `synthetic_declarations` (I-18) |

## Autonomy

"Skip synthetic-content label" is `blocked` in **every** autonomy mode, including `MANUAL`
(`BLUEPRINT/05`). There is no approval that grants it. An operator who wants to publish unlabelled
synthetic content must do so outside this system, which is the correct place for that decision to live.
