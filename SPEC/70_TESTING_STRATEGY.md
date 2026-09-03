# 70 — Testing Strategy

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

> This is the **single** testing strategy document. V2 shipped two overlapping ones with different scope
> and different acceptance criteria (D-022).

## Layers

| Layer | Scope | Runs |
|---|---|---|
| Unit | Pure domain logic, scoring, state guards, money arithmetic | Every build |
| Contract | Every JSON Schema against positive and negative instances | Every build |
| Integration | Database, migrations, transactions, artifact store | Every build |
| Adapter contract | Every provider and platform adapter against the shared suite | Every build, with fakes |
| Media | FFmpeg invocation, probing, profile validation | Every build |
| Security | `SPEC/72` | Every build |
| Concurrency | `SPEC/73` | Every build |
| Chaos | `SPEC/74` | Nightly and before release |
| Acceptance | `SPEC/75` | Before release |
| Packaging | Install, upgrade, uninstall, restore | Before release |

Everything runs offline. A test suite that needs the internet cannot honestly test network failure,
because it cannot distinguish the failure it injected from the one it suffered.

## The fake discipline

A fake that models only success is worse than no fake, because it manufactures confidence. Every provider
and platform fake MUST model: success, 401, 403, 404, 429 with and without `Retry-After`, 500, timeout
before send, timeout after send, malformed body, partial upload, and duplicate idempotency key.

"Timeout after send" is the case the whole architecture is built around. A fake that cannot produce it
cannot exercise the code that matters.

## Invariant coverage

**Every invariant in `BLUEPRINT/10` has at least one adversarial test that tries to break it.**
An invariant with no adversarial test is a hope with a table row. `SPEC/71` maps invariants to tests.

## Coverage policy

Line coverage is a diagnostic, not a target. The binding requirements are: every invariant has an
adversarial test, every transition in `SPEC/13` has a test, every error code has a test that produces it,
and every schema conditional has a negative instance that must fail.

## Determinism

`IClock` and `IFileSystem` are ports so that lease expiry, retention, budget windows and approval expiry
are testable without waiting. A test that sleeps is a test that will eventually be flaky and then be
disabled.
