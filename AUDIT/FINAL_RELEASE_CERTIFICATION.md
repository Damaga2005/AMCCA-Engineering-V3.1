# AMCCA Engineering V3.1 — Final Release Certification

> **CERTIFICACIÓN DE EMISIÓN DE RELEASE:** SIXTH-AUDIT CLOSURE (S1–S6)
> (bootstrap verbs gated behind `AMCCA_ALLOW_BOOTSTRAP` outside DEBUG; `--probe` no longer flips
> `autonomy_mode`; provider error bodies sanitized before the log; D-037 flag reaches both adapters +
> `reasoning_model` cross-field rule; `OmniRoutersGatewayAdapter.cs` back to LF; timestamped agent
> transcript logs — on top of D-037 reasoning-model shaping, orchestrator resilience, and the
> fifth-audit remediation H1/M4/M3/L1/M5/M1)
> **FECHA DE EMISIÓN:** 2026-09-07
> **ESTADO OFICIAL:** **RELEASE PASS**

This certification supersedes the `bf8fb50` certification (CI run `34146788799`), invalidated under its
own integrity rule #4 by PR #10, which closes the six findings of `AUDIT/SIXTH_AUDIT_CODE.md` — a
delta audit of the PR #6–#9 surface (local bootstrap CLI, orchestrator resilience, D-037
reasoning-model shaping, the real-run fixes). `AUDIT/FIFTH_AUDIT_CODE.md` carries the fifth-audit
findings; `AUDIT/SIXTH_AUDIT_CODE.md` the sixth.

---

## 1. Identificación y Metadatos de la Versión

- **Repository:** `Damaga2005/AMCCA-Engineering-V3.1`
- **Branch:** `main`
- **Source SHA certified:** `1e513ce375d2416d7e118d630745315cfae2c0f5`
  (merge commit of PR #10 `fix/sixth-audit-findings` → `main`)
- **Documentary commit:** this commit (the one adding this document); NOT the certified source SHA.
- **CI run:** GitHub Actions Run `34150914513` — <https://github.com/Damaga2005/AMCCA-Engineering-V3.1/actions/runs/34150914513>
- **CI commit SHA:** `1e513ce375d2416d7e118d630745315cfae2c0f5`
- **Source SHA == CI Commit SHA:** PASS (`HEAD valid: PASS (1e513ce375)` in the certification pipeline)
- **CI conclusion:** `success` — both jobs green, every step green.
- **Certification model:** the release SHA is the immutable source/artifact commit tested by CI.
  This document is evidence committed afterwards; it is not the source/artifact identity and must
  not be described as CI-certified itself.
- **Build:** `net8.0-windows` / `Release` (Self-Contained `win-x64`)
- **Tests (release `.trx`, verified by the certification pipeline):** `776 total | 776 passed | 0 failed | 0 skipped`
- **Build diagnostics (`build_diagnostics.json`, verified by the pipeline):** `0 errors | 0 warnings`
- **Installer artifacts (verified present, non-zero, PE32+ valid, SHA256SUMS.txt consistent):**
  `AMCCA-Setup.exe` 62,399,723 B · `AMCCA-Setup.msi` 61,584,344 B · `AMCCA-Desktop-win-x64.zip` 72,878,028 B.
- **Installer artifact hashes (MSI / EXE / ZIP SHA-256):** recorded in the run's `SHA256SUMS.txt`
  produced by the "Run Deterministic Release Certification Pipeline" step of CI run `34150914513`.
  Not transcribed here — the run's logs and artifacts require GitHub authentication to read, and this
  document does not copy hashes from a partial log view.

## 2. CI Evidence — GitHub Actions Run `34150914513` (commit `1e513ce`)

### Job: `validate-spec` (Ubuntu) — conclusion `success`

| Step | Result |
|---|---|
| Install pinned dependencies | success |
| Structural, contract and drift checks (`validate_package.py`, 68/68) | success |
| Conformance tests (schema conditionals, positive/negative cases, 65/65) | success |
| Repository hygiene check (`test_repository_hygiene.py`) | success |
| Specification mutation tests (19/19) | success |
| Adversarial certification mutation tests (15/15) | success |
| Generated-artifact drift check (`--check` only, never `--regen`) | success |
| Release gate (`release_gate.py`) | success |

### Job: `Windows Desktop & WPF Solution Validation` — conclusion `success`

| Step | Result |
|---|---|
| Validate package, conformance, hygiene and mutations on Windows | success |
| Restore .NET dependencies | success |
| Build .NET solution (Release) — 0 warnings | success |
| Verify `AMCCA.exe` binary generated and functional | success |
| Install WiX Toolset | success |
| Build WiX Installer (`AMCCA-Setup.msi` and `AMCCA-Setup.exe`) | success |
| Run .NET test suites (Core, Concurrency, Chaos, OAuth, WPF MVVM) | success |
| **Run Deterministic Release Certification Pipeline** (`release_certification.ps1`) — `CERTIFICATION COMPLETE: RELEASE PASS`, all 15 release invariants strict | success |

## 3. Changes since the previous certification

**Sixth audit (PR #10) — the change in this certification.** A delta audit of the PR #6–#9 surface
(`AUDIT/SIXTH_AUDIT_CODE.md`) found six issues, all closed here. None invalidated `bf8fb50` alone;
the PR touches code, so integrity rule #4 applies.

| # | Sev | Change | Basis |
|---|---|---|---|
| **S1** | Med | `--probe` sets only `capabilities_verified` (the evidence D-028 asks for); it no longer flips `autonomy_mode → AUTONOMOUS` for the operator. Regex anchored (`^\s*`, multiline) so a commented line cannot match. | D-028 |
| **S2** | Med | `Program.cs` gates the `--set-secret` / `--probe` / `--seed-demo` bootstrap verbs behind `AMCCA_ALLOW_BOOTSTRAP=1` in non-`DEBUG` builds — they mutate `config.yaml` and write demo rows into the real DB, so the shipped installer must not expose them by accident. | `CLAUDE.md` (no arbitrary DB rows / secrets handling) |
| **S3** | Med | New `ProviderErrorText.Sanitize`: redacts the resolved key and every bearer/`sk-`/`api_key`-shaped substring and caps length before a provider error body enters an `AmccaException` message (which reaches the Serilog file log and the tick report). Applied in `DirectOpenAiCompatibleGatewayAdapter`'s 4xx path and `ResilientProviderGateway`'s circuit-open fault. Same posture as `SafeOAuthError` (SEC-10). | SEC-10, `CLAUDE.md` (no secrets in logs) |
| **S4** | Low | `OmniRoutersGatewayAdapter` takes the `reasoningModel` flag too (was direct-adapter only). `ConfigService` rule 6 rejects `reasoning_model: true` without a `default_model_id` (`Cfg004`) instead of letting the flag be a silent no-op. Addendum to D-037. | D-037 |
| **S5** | Low | `dos2unix src/AMCCA.Core/Providers/OmniRoutersGatewayAdapter.cs` — it was the only CRLF file in `src/` and `.gitattributes` `* -text` made that permanent. One-time diff cost. | repo LF convention |
| **S6** | Low | `AgentTranscriptLog` names the file with a UTC timestamp so a stage rerun after rework does not overwrite the failed run's transcript. | — |

New tests: `AiProviderRealIntegrationTests.OmniRouters_OmitsTemperature_ForAReasoningModel`,
`AiProviderRealIntegrationTests.ErrorBody_IsRedactedAndCapped_BeforeItReachesTheExceptionMessage`,
`ConfigurationContractTests.ReasoningModelWithoutDefaultModelId_AbortsWithCfg004`. No schema change —
`reasoning_model` has been in `config.schema.json` since PR #8; S4 adds only a cross-field code rule.

**Carried in from the `bf8fb50` certification (PR #7 / #8 / #9).** Found by connecting a real model
provider and driving the orchestrator through a full run — Groq, then Gemini, then OpenAI `gpt-4o`;
that last run completed the reachable pipeline `RESEARCHING → RESEARCH_VERIFIED → CONCEPT_SELECTED →
SCRIPTING → SCRIPT_VERIFIED → STORYBOARDING → BLOCKED (AMCCA-MED-001)` (real model calls, real web
sources, a VERIFIED claim, a reserved scripting budget, a schema-valid SCRIPT artifact, `cost_events`
rows for both agent runs; stops at `STORYBOARDING` because no image/audio provider is wired).

| PR | Change |
|---|---|
| **#7** | Retryable 5xx (`ErrorCategory.Transient` → `ResilientProviderGateway` retries with backoff); 4xx errors carry the provider's body; `ORC-002` failure detail logged; `--set-secret`/`--probe` use the same secret-store dir as `--orchestrator`; `RunOrchestrator` expands env vars in `data_root`; **D-036** `default_model_id` (closes fifth-audit L3). |
| **#8** | **D-037** reasoning-model request shaping: `config.providers.gateway.reasoning_model` (omit `temperature`); every request uses `max_completion_tokens` not the deprecated `max_tokens`; probe likewise. |
| **#9** | Per-turn ceiling 2048 → 16384; an eager model that tries to finish before calling any tool is nudged back to its tools twice; `AgentResearchAgent`/`AgentScriptAgent` dump the run transcript to `%LOCALAPPDATA%/AMCCA/logs/`; the research and script prompts spell out the exact tool / line-object schemas and the PRIMARY/SECONDARY trust-tier rule; `fetch_source` is idempotent on a re-fetched URL. |

**Fifth-audit remediation (carried in, certified since `34c3cb8`).** Full findings matrix, P0/P1/P2
classification and `CLAUDE.md` "Never do" review in `AUDIT/FIFTH_AUDIT_CODE.md`:

| Finding | Change | Contract basis |
|---|---|---|
| **H1** (P0) — no AI-spend cost accounting | `config.providers.gateway.model_pricing` (D-034) → `pricing_snapshots` → `decimal` per-turn cost (`ModelCostCalculator`) → folded into `AgentRunSession.AccumulatedCost` so `contract.MaxCost` enforces model spend → one `SETTLEMENT` `cost_events` row per run (`RECONCILED` with a snapshot, `ESTIMATED_UNRECONCILED` without — never a silent zero or an invented price). Ports `IModelPricing`/`IModelCostStore`. | SPEC/20, SPEC/21, `cost-event.schema.json`, D-023/D-031 |
| **M4** (P1) — `CONCEPT_SELECTED` was a no-op | `ConceptSelectionStageHandler` (D-035): select the operator's opportunity or the highest **pre-computed** score in AUTONOMOUS, reserve the scripting budget, persist the decision + EV snapshot — or `BLOCKED` with a SPEC/05 reason code. Never `NoWorkAdvanceHandler`. | SPEC/12, SPEC/13 T-003/T-004, SPEC/29, DEF-008 |
| **M3** (P1) — WPF async errors vanished | Global `DispatcherUnhandledException` / `UnobservedTaskException` / `AppDomain` handlers + file-backed Serilog logger; the one unguarded `await` in `OnStartup` wrapped. | SPEC/60 obligation 6 |
| **L1** (P1) — OAuth revocation failure swallowed | `RevokeTokenAsync` narrows its catch to `HttpRequestException`/`TaskCanceledException` and writes an `OAUTH_REVOKED` `audit_log` row (`ALLOWED` / `ERROR`); local disconnect still unconditional. | SPEC/43 |
| **M5** (P2) — no end-to-end real-agent pipeline test | `AgentPipelineEndToEndTests`: one production driven through `OrchestratorEngine.RunTickAsync` across the real RESEARCHING → RESEARCH_VERIFIED → CONCEPT_SELECTED → SCRIPTING → SCRIPT_VERIFIED chain with the real `AgentResearchAgent` / `ConceptSelectionStageHandler` / `AgentScriptAgent` against a scripted gateway; asserts real transitions, a persisted SCRIPT artifact, concept locked + budget reserved, and a settled `RECONCILED` cost row. | — |
| **M1** (P2) — clock not injectable | `TimeProvider` injected into the components with real time-comparison logic (`ApprovalManager` expiry, `OAuthManager` token `expires_at`, `MemoryRetrievalService` recency decay); `TimeProvider.System` DI singleton; `ApprovalExpiryClockTests` proves `FakeTimeProvider` control. Write-only `created_at`/`updated_at` sites left on the system clock (no assertable path), seam now in place. | — |

Findings closed by analysis (no code change): **H2** (already guarded by `validate_package.py`'s live
schema build), **M2** (deliberate poison-job protection), **L2** (`net8.0-windows` TFM), **L3**
(already `ponytail:`-flagged). **All ten fifth-audit findings are closed.**

## 4. Security Hardening — SEC-01 → SEC-11 (carried forward, re-verified)

The SEC-01 → SEC-11 controls (certified since `9ba76f4`) are unchanged in this release. PR #10 extends
the SEC-10 posture (never echo a remote error body) to the model-provider path: `ProviderErrorText`
redacts and caps a 4xx body before it can reach the log, where before it was passed through with only
a length cap. No SEC control is weakened. The one security-adjacent file touched by the fifth-audit
remediation is `OAuthManager.RevokeTokenAsync` (L1):
`ValidateOAuthEndpoint(revocationEndpoint, "revocation")` still runs `SsrfValidator` before any
connection (**SEC-03** intact), `SafeOAuthError` is untouched (**SEC-10** intact), and no production
`HttpClient` parameter was introduced (**SEC-11** intact). The change only narrows a bare `catch` and
adds an audit row.

| Control | Verdict | Correction (production code) |
|---|---|---|
| **SEC-01** SecretRef / API key | PASS | Provider gateways resolve credentials `SecretReference → ISecretStore → Bearer`; a literal key is rejected at construction with `AMCCA-SEC-002`; missing secret fails closed with `AMCCA-AI-001` before any HTTP. |
| **SEC-02** OAuth HTTP client / SSRF bypass | PASS | `OAuthManager` takes `ISafeHttpClientFactory` only; no arbitrary `HttpClient`; client created per call from `SafeHttpClientFactory.Default`. |
| **SEC-03** OAuth token endpoint validation | PASS | `ValidateOAuthEndpoint()` runs `SsrfValidator.ValidateDestinationUri` on authorization / token / refresh / revocation endpoints before any connection. |
| **SEC-04** OAuth redirect hardening | PASS | `SafeRedirectHandler`: `AllowAutoRedirect=false`, per-hop SSRF re-validation, `Authorization`/`Host` stripped across hops, 5-hop cap → `AMCCA-SEC-003`. |
| **SEC-05** InMemorySecretStore production misuse | PASS | `SecretStoreGuard.EnsureProductionGrade` rejects ephemeral/absent store with `AMCCA-SEC-002`; invoked in `App.OnStartup` before migrations; production registers `WindowsDpapiSecretStore`. |
| **SEC-06** Agent cost reservation ordering | PASS | `AgentRuntime.ExecuteToolCallAsync` reserves cost only after authorization, tool existence, side-effect gate and intent checks; `AgentRunSession.ReleaseCost` rolls back on throw/cancellation. |
| **SEC-07** Agent output resource exhaustion | PASS | `EnforceOutputResourceLimits` bounds size (512 KB), depth (64), property count (10 000), array length (10 000), string length (100 000) before schema evaluation; controlled `AMCCA-AI-003`. |
| **SEC-08** Archive extraction transactional cleanup | PASS | Extraction into `__amcca_staging_<guid>/`; validate every entry; commit only on full success; any failure deletes staging. |
| **SEC-09** Windows symlink/junction/reparse-point confinement | PASS | `PathConfinement.EnsureConfinedNoReparsePoint` rejects any reparse point between root and candidate; wired into `MediaRenderer`, `SafeArchiveExtractor`, staging→target commit. |
| **SEC-10** OAuth remote error disclosure | PASS | `SafeOAuthError` echoes only a whitelisted short alphanumeric OAuth2 `error` code with HTTP status and provider; never the raw body, tokens, headers, cookies or stack traces. |
| **SEC-11** HttpClient injection bypass | PASS | `OAuthManager` and the provider gateways: no production `HttpClient` param (test-only `internal` ctor via `InternalsVisibleTo`); all outbound calls run through `SsrfValidator` + `SafeRedirectHandler` + coupled-DNS `ConnectCallback`. |

SEC-12 → SEC-20 regression review (OAuth callback binding, DB security, agent tool authorization,
EXTERNAL_UNSAFE gate, SSRF DNS rebinding, PKCE/state, ZIP-bomb controls, `SecretReference` format,
cancellation/timeouts): all intact.

## 5. Security Regression Tests

`SecretRefResolutionRegressionTests`, `OAuthSsrfAndDisclosureRegressionTests`,
`ProductionSecretStoreRegressionTests`, `AgentCostReservationOrderRegressionTests`,
`AgentOutputResourceLimitRegressionTests`, `ArchiveExtractionStagingRegressionTests`,
`ReparsePointConfinementRegressionTests`, `PlatformAdapterSsrfRegressionTests`,
`PlatformOAuthContractTests` — positive and negative cases reaching real production code paths.
Fifth-audit additions: `AgentCostAccountingTests` (7), `ConceptSelectionGateTests` (6),
`AgentPipelineEndToEndTests` (1, M5), `ApprovalExpiryClockTests` (1, M1), and two new
`PlatformOAuthContractTests` cases for the revocation-audit path. No existing test removed or weakened.

## 6. Reglas de Integridad de la Certificación

1. `1e513ce375d2416d7e118d630745315cfae2c0f5` es el **release source SHA** certificado.
2. El commit que añade este documento contiene evidencia documental; NO es el source SHA certificado.
3. No se debe afirmar que el documentary commit fue ejecutado por el CI citado aquí
   (run `34150914513` corresponde a `1e513ce`).
4. Toda futura modificación de código, workflow, tooling, manifiestos o artefactos invalida esta
   certificación hasta ejecutar de nuevo el proceso completo.
5. Una certificación posterior debe identificar explícitamente el nuevo source SHA y su run de CI exacto.
6. Los hashes SHA-256 de MSI/EXE/ZIP para este release se leen del `SHA256SUMS.txt` producido por
   CI run `34150914513`; este documento no los transcribe para no arrastrar hashes de una vista parcial.

## 7. Dictamen Final

Bajo la regla:

`IMPLEMENTACIÓN REAL + TEST ADVERSARIAL + INTEGRACIÓN REAL + EVIDENCIA REPRODUCIBLE`

el **source commit** `1e513ce375d2416d7e118d630745315cfae2c0f5` queda certificado como **RELEASE PASS**:

- CI run `34150914513` verde en ambos jobs (Ubuntu spec + Windows desktop/WPF/WiX/certification
  pipeline), con `CI commit SHA == source SHA`.
- Los diez findings de la quinta auditoría cerrados: H1/M4/M3/L1/M5 con código, M1 con código
  (parcial deliberado: lógica de comparación temporal), H2/M2/L2/L3 por análisis.
  `CLAUDE.md` "Never do" revisado punto por punto (`FIFTH_AUDIT_CODE.md` §3).
- Los seis findings de la sexta auditoría (S1–S6) cerrados en PR #10: S1/S2/S3 con código
  (autonomía no auto-escalada, verbos de bootstrap con guarda, cuerpo de error del proveedor
  saneado), S4 con código + regla de config, S5/S6 higiene. `SIXTH_AUDIT_CODE.md` §3.
- SEC-01 → SEC-11 sin cambios y re-verificados; SEC-12 → SEC-20 intactos; postura SEC-10 extendida
  a la ruta del proveedor de modelo (S3).
- 776/776 tests (release `.trx`), build 0 errores / 0 avisos, `validate_package` 68/68,
  `conformance` 65/65, spec mutations 19/19, certification mutations 15/15, `release_gate` PASS,
  `release_certification.ps1` → `CERTIFICATION COMPLETE: RELEASE PASS` (15/15 invariantes estrictos).

**VERDICT: RELEASE PASS**
