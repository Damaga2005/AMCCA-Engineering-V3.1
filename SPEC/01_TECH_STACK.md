# 01 — Technology Stack

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

The stack is fixed by `DECISIONS.md`. This file states the versions and the reasons, so that a future
contributor can tell the difference between a decision that was reasoned and one that was inherited.

## Fixed choices

| Concern | Choice | Locked by | Reason |
|---|---|---|---|
| Language / runtime | C# on .NET 8 LTS | D-002 | LTS window, first-class Windows integration, DPAPI access without interop gymnastics |
| UI | WPF with MVVM | D-002 | Mature data binding for an inspector-heavy operational UI; no web runtime to secure |
| Embedded browser | WebView2, only for OAuth and provider consoles | D-002 | Some providers have no headless auth path; scope is deliberately minimal |
| Database | SQLite (WAL, `foreign_keys=ON`) | D-003 | Single-user, local-first, transactional, no server to operate or secure |
| Data access | Dapper + Microsoft.Data.Sqlite | D-003 | Explicit SQL; the transaction boundaries in `SPEC/11` must be visible in the code, which an ORM hides |
| Identifiers | ULID | D-003 | Sortable, locally generated, no coordination, no external-key coupling |
| JSON | System.Text.Json | D-004 | In-box, source-generatable, no third-party deserialisation surface |
| Schema validation | JSON Schema draft 2020-12 | D-004 | The contracts in `SCHEMAS/` are the boundary; a library-specific attribute model is not portable |
| HTTP | HttpClientFactory typed clients | D-005 | Correct socket lifetime; policy attaches at registration, not at call sites |
| Resilience | Polly | D-006 | Retry, timeout, circuit breaker, rate limit as declared policies |
| Logging | Serilog | D-007 | Structured sinks and a redaction stage that runs before every sink |
| Media | FFmpeg as a child process | D-008 | No in-process codec surface; a crash in decoding kills a child, not the host |
| Secrets | DPAPI / Windows Credential Manager | D-009 | OS-managed key material; no key file for us to leak |
| Installer | WiX Toolset | D-010 | Deterministic MSI, upgrade codes, per-machine and per-user handling |
| Tests | xUnit + FluentAssertions + Testcontainers-free local fakes | — | Everything runs offline; a test suite that needs the internet cannot test network failure honestly |
| Money | `decimal` in code, decimal string in storage | D-023 | Binary floating point cannot represent 0.1; budgets that drift are budgets that fail open |

## Explicitly rejected

| Rejected | Why |
|---|---|
| Electron / Tauri | A second runtime and a second security surface for no gain in an operational desktop tool |
| Python as a runtime foundation | Deployment and dependency isolation on Windows; permitted for build tooling only |
| Entity Framework Core in the core | Hides transaction boundaries and query shape, both of which are normative here |
| An in-process media library | Codec parsing is a memory-safety surface; isolate it in a child process |
| Floating-point money | See D-023 |
| A message broker | Single-process product; a broker adds an operational dependency D-001 forbids |

## Version policy

Pin exact versions in a lock file. Update in controlled batches with a changelog entry. A production
build fails if a dependency has an advisory above the configured severity threshold (`SPEC/59`).
No dependency may silently introduce a required external service.
