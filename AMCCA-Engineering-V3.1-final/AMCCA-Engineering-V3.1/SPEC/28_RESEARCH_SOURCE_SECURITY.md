# 28 — Research Source Security

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

This is the highest-risk inbound path in the system: it retrieves content an adversary can influence and
feeds it to a model that will act on it. It deserves more care than it usually gets.

## SSRF defence

1. Parse and canonicalise the URL. Reject non-HTTP(S) schemes.
2. Resolve DNS **and validate the resolved addresses**, not the hostname.
3. Reject private, loopback, link-local, multicast and reserved ranges.
4. Pin the validated address for the connection to prevent DNS rebinding between check and connect.
5. Re-validate on every redirect. A redirect is a new request, not a continuation.
6. Cap redirect depth.

Rejection raises `AMCCA-SEC-003` and is audited.

## Resource limits

Maximum response bytes, maximum total time, maximum decompressed size for compressed responses,
content-type allow-list, and rejection of responses whose declared and sniffed types disagree.

## Prompt-injection resistance

Retrieved content is untrusted input, not instruction. Concretely:

1. Content is passed to the model in a clearly delimited data region, never concatenated into the
   instruction region.
2. The agent contract states that retrieved content cannot alter the agent's task, tool permissions or
   output schema.
3. The output is schema-validated regardless of what the content said. A document instructing the model to
   return prose gets a schema violation, not compliance.
4. Tool permissions are evaluated by the runtime from the agent contract, so no instruction inside content
   can grant a tool.
5. Claims derived from a single source that also contains instruction-like patterns are flagged for review.

Point 4 is the structural defence. The others reduce the chance of a bad proposal; only point 4 makes a
successful injection unable to do anything.

## Content hygiene

Strip active content before storage. Never execute, render or evaluate retrieved content. Store by hash.
Treat filenames and metadata from retrieved content as untrusted strings.
