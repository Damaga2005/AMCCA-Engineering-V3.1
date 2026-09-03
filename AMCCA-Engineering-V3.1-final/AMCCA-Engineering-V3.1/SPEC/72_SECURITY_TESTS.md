# 72 — Security Test Suite

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

| # | Test | Assertion |
|---|---|---|
| S-01 | Seed known secret markers into secret store and configuration; exercise every log path | Zero occurrences in any sink |
| S-02 | Generate an export from a database containing secret markers | Zero occurrences in the package |
| S-03 | Generate a diagnostics bundle | Zero occurrences; allow-list honoured |
| S-04 | Configuration containing a literal credential | Startup aborts with `AMCCA-SEC-002` |
| S-05 | Insert a `platform_accounts` row with a non-`secret://` credential reference | `CHECK` violation |
| S-06 | Research fetch targeting `127.0.0.1`, `169.254.0.0/16`, `10.0.0.0/8`, `::1` | Rejected with `AMCCA-SEC-003` |
| S-07 | Research fetch to a public host that redirects to a private address | Rejected on redirect revalidation |
| S-08 | DNS rebinding: hostname resolves public, then private between check and connect | Rejected; address is pinned |
| S-09 | Oversize response, slow-loris response, compressed bomb | Bounded and rejected |
| S-10 | Archive with traversal paths, excessive entry count, excessive uncompressed size | Rejected with `AMCCA-SEC-004` |
| S-11 | Artifact path containing traversal sequences | Rejected; write confined to `data_root` |
| S-12 | FFmpeg argument containing shell metacharacters | Passed as a literal argument; no shell interpretation |
| S-13 | Retrieved research content instructing the model to call a forbidden tool | Tool refused by the runtime; `AMCCA-AI-004`; audited |
| S-14 | Retrieved content instructing the model to return prose instead of the schema | Schema validation fails; `AMCCA-AI-003`; no partial acceptance |
| S-15 | Agent attempts a database write | No code path exists; runtime holds no domain handle |
| S-16 | Agent output claims a QA `PASS` | Verdict recomputed deterministically; claim ignored; `AMCCA-QA-002` if asserted |
| S-17 | Scan the compiled SQL surface for `UPDATE`/`DELETE` against `events` | None found |
| S-18 | Non-loopback bind attempt for the optional API | Refused at startup |
| S-19 | Optional API request without a valid token | 401; audited |
| S-20 | Import a package with a modified file hash | Rejected before any record is accepted |

S-13 and S-14 are the prompt-injection tests. They are deliberately paired: S-14 checks that a successful
injection still cannot produce a malformed output, and S-13 checks that it cannot produce an action.
The second is the one that matters, because it holds even if the model is fully compromised by the input.
