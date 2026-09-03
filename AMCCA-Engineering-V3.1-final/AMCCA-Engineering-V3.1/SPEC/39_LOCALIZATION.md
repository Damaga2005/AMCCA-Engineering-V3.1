# 39 — Localisation

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Scope

Two separate concerns that are often conflated: the language of the **interface** and the language of the
**content**.

## Content language

`productions.language` is set at creation and constrains research source selection, script generation,
voice model selection, caption language, disclosure wording and platform metadata.

Disclosure text is the sensitive part: an affiliate or synthetic-content disclosure must be in the language
of the content and phrased acceptably for the target jurisdiction and platform. Disclosure strings are
therefore versioned per language with a `source_ref`, not machine-translated at runtime.

## Interface language

Operator-facing strings are resourced. Error **codes** are never localised; only their human messages are.
A support conversation is conducted in codes.

## Numbers, dates and currency

Timestamps are stored in UTC and rendered in the operator's local timezone with the offset visible.
Money is stored as a decimal string with an explicit currency and rendered per locale. A number rendered
without its currency is a defect.

## Right-to-left and non-Latin scripts

Where supported, the UI must handle bidirectional text and non-Latin scripts in operator-visible fields,
including in artifact titles and platform metadata previews. Caption and safe-area validation accounts for
script-dependent text metrics rather than assuming Latin character widths.
