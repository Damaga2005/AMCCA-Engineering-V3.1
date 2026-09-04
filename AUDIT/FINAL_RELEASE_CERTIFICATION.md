# AMCCA Engineering V3.1 — Final Release Certification

> **CERTIFICACIÓN DE EMISIÓN DE RELEASE:** SEC-01 → SEC-11 SECURITY HARDENING + POST-FIX AUDIT
> **FECHA DE EMISIÓN:** 2026-09-04
> **ESTADO OFICIAL:** **RELEASE PASS**

This certification supersedes the previous one (source SHA `782dd9b7f98c637cc92cffd9dcbad9059acf6f39`,
CI run `33860326908`), which was invalidated under its own integrity rule #4 by the SEC-01 → SEC-11
security-hardening commit series.

---

## 1. Identificación y Metadatos de la Versión

- **Repository:** `Damaga2005/AMCCA-Engineering-V3.1`
- **Branch:** `add_amcca_engineering_repo`
- **Source SHA certified:** `9ba76f4593034632d59070b5bb73e9e4f99ff04d`
- **Documentary commit:** this commit (the one adding this document); NOT the certified source SHA.
- **CI run:** GitHub Actions Run `33875306007` — <https://github.com/Damaga2005/AMCCA-Engineering-V3.1/actions/runs/33875306007>
- **CI commit SHA:** `9ba76f4593034632d59070b5bb73e9e4f99ff04d`
- **Source SHA == CI Commit SHA:** PASS
- **CI conclusion:** `success` — both jobs green, every step green.
- **Certification model:** the release SHA is the immutable source/artifact commit tested by CI.
  This document is evidence committed afterwards; it is not the source/artifact identity and must
  not be described as CI-certified itself.
- **Build:** `net8.0-windows` / `Release` (Self-Contained `win-x64`)
- **Tests (local reproduction on the certified SHA):** `612 passed, 0 failed, 0 skipped`
  (`dotnet test AMCCA.sln -c Release`)
- **Build diagnostics (local reproduction):** `0 errors, 0 warnings` (clean `dotnet build -c Release`)
- **Installer artifact hashes (MSI / EXE / ZIP SHA-256):** recorded in the run's
  `SHA256SUMS.txt` produced by the "Build WiX Installer" step of CI run `33875306007`.
  Not transcribed here — the run's logs and artifacts require GitHub authentication to read,
  and this document does not copy hashes from a different build.

## 2. CI Evidence — GitHub Actions Run `33875306007` (commit `9ba76f4`)

### Job: `validate-spec` (Ubuntu) — conclusion `success`

| Step | Result |
|---|---|
| Install pinned dependencies | success |
| Structural, contract and drift checks (`validate_package.py`) | success |
| Conformance tests (schema conditionals, positive/negative cases) | success |
| Repository hygiene check (`test_repository_hygiene.py`) | success |
| Specification mutation tests (15/15) | success |
| Adversarial certification mutation tests (15/15) | success |
| Generated-artifact drift check (`--check` only, never `--regen`) | success |
| Release gate (`release_gate.py`) | success |

### Job: `Windows Desktop & WPF Solution Validation` — conclusion `success`

| Step | Result |
|---|---|
| Validate package, conformance, hygiene and mutations on Windows | success |
| Restore .NET dependencies | success |
| Build .NET solution (Release) | success |
| Verify `AMCCA.exe` binary generated and functional | success |
| Install WiX Toolset | success |
| Build WiX Installer (`AMCCA-Setup.msi` and `AMCCA-Setup.exe`) | success |
| Run .NET test suites (Core, Concurrency, Chaos, OAuth, WPF MVVM) | success |
| **Run Deterministic Release Certification Pipeline** (`release_certification.ps1`) | success |

## 3. Security Hardening Closure — SEC-01 → SEC-11

Independent post-fix security audit performed on this exact SHA; full matrix in
`AMCCA ENGINEERING V3.1 — POST-FIX SECURITY AUDIT` (see conversation record / repo history).

| Control | Verdict | Correction (production code) |
|---|---|---|
| **SEC-01** SecretRef / API key | PASS | Provider gateways resolve credentials `SecretReference → ISecretStore → Bearer`; a literal key is rejected at construction with `AMCCA-SEC-002`; missing secret fails closed with `AMCCA-AI-001` before any HTTP. |
| **SEC-02** OAuth HTTP client / SSRF bypass | PASS | `OAuthManager` takes `ISafeHttpClientFactory` only; no arbitrary `HttpClient`; client created per call from `SafeHttpClientFactory.Default`. |
| **SEC-03** OAuth token endpoint validation | PASS | `ValidateOAuthEndpoint()` runs `SsrfValidator.ValidateDestinationUri` on authorization / token / refresh / revocation endpoints before any connection. |
| **SEC-04** OAuth redirect hardening | PASS | `SafeRedirectHandler`: `AllowAutoRedirect=false`, per-hop SSRF re-validation, `Authorization`/`Host` stripped across hops, 5-hop cap → `AMCCA-SEC-003`. |
| **SEC-05** InMemorySecretStore production misuse | PASS | `InMemorySecretStore : IEphemeralSecretStore`; `SecretStoreGuard.EnsureProductionGrade` rejects ephemeral/absent store with `AMCCA-SEC-002`; invoked in `App.OnStartup` before migrations; production registers `WindowsDpapiSecretStore`. |
| **SEC-06** Agent cost reservation ordering | PASS | `AgentRuntime.ExecuteToolCallAsync` reserves cost only after authorization, tool existence, side-effect gate and intent checks; `AgentRunSession.ReleaseCost` rolls back on throw/cancellation. |
| **SEC-07** Agent output resource exhaustion | PASS | `EnforceOutputResourceLimits` bounds size (512 KB), depth (64, via `JsonDocumentOptions`), property count (10 000), array length (10 000), string length (100 000) before schema evaluation; controlled `AMCCA-AI-003`, no OOM/stack overflow. |
| **SEC-08** Archive extraction transactional cleanup | PASS | Extraction into `__amcca_staging_<guid>/`; validate every entry; commit only on full success; any failure deletes staging → a rejected archive never touches the target. Residual: per-file `File.Move` commit is not filesystem-atomic across files (documented, acceptable). |
| **SEC-09** Windows symlink/junction/reparse-point confinement | PASS | `PathConfinement.EnsureConfinedNoReparsePoint` rejects any reparse point between root (exclusive) and candidate (inclusive); wired into `MediaRenderer`, `SafeArchiveExtractor` entry validation, and the staging→target commit. |
| **SEC-10** OAuth remote error disclosure | PASS | `SafeOAuthError` echoes only a whitelisted short alphanumeric OAuth2 `error` code with HTTP status and provider; never the raw body, tokens, headers, cookies or stack traces. |
| **SEC-11** HttpClient injection bypass | PASS | `OAuthManager` and the provider gateways: no production `HttpClient` param (test-only `internal` ctor via `InternalsVisibleTo`). `BasePlatformAdapter` and the YouTube/TikTok/Instagram/Twitter adapters obtain their transport from `ISafeHttpClientFactory` (default `SafeHttpClientFactory.Default`); all outbound calls and redirects run through `SsrfValidator` + `SafeRedirectHandler` + coupled-DNS `ConnectCallback`. |

Regression review SEC-12 → SEC-20 (OAuth callback binding, DB security, agent tool authorization,
EXTERNAL_UNSAFE gate, SSRF DNS rebinding, PKCE/state, ZIP-bomb controls, `SecretReference` format,
cancellation/timeouts): all intact; SEC-14/15/16/17/19 strengthened (more consumers forced through
the SSRF-safe pipeline and the secret-reference contract).

Global bypass search over `src/`: every `new HttpClient(` / `HttpClient?` / `Dns.GetHostAddresses` /
`InternalsVisibleTo` / `InMemory` occurrence explained — protected, test-only, or benign.
No unexplained security-sensitive occurrence.

## 4. Security Regression Tests (added by the SEC series)

`SecretRefResolutionRegressionTests`, `OAuthSsrfAndDisclosureRegressionTests`,
`ProductionSecretStoreRegressionTests`, `AgentCostReservationOrderRegressionTests`,
`AgentOutputResourceLimitRegressionTests`, `ArchiveExtractionStagingRegressionTests`,
`ReparsePointConfinementRegressionTests`, `PlatformAdapterSsrfRegressionTests` —
positive and negative cases per SEC, reaching real production code paths (real `SecretReference.Parse`,
real `SafeHttpClientFactory`, real `SafeRedirectHandler`, real `PathConfinement`, real `AgentRuntime`).
No existing test was removed or weakened.

## 5. Manifest / Line-Ending Remediation

Commits `7fb7d31` and `61e1d8a` regenerated `MANIFEST.md` / `MANIFEST.sha256` on a Windows checkout
with `core.autocrlf=true`, so content hashes were computed over CRLF-materialised text. `.gitattributes`
is `* -text`, so CI (Ubuntu) hashes the verbatim LF blobs — 276 entries mismatched and both CI jobs
failed fast (run `33873448498`, commit `61e1d8a`). Commit `9ba76f4` sets `core.autocrlf=false`,
re-materialises the tree verbatim, and regenerates the manifest via `TOOLS/validate_package.py --regen`.
All 357 entries now equal their git-blob SHA-256. No source or test file changed in that commit.

## 6. Reglas de Integridad de la Certificación

1. `9ba76f4593034632d59070b5bb73e9e4f99ff04d` es el **release source SHA** certificado.
2. El commit que añade este documento contiene evidencia documental de la certificación del source SHA anterior.
3. No se debe afirmar que el documentary commit fue ejecutado por el CI citado en esta certificación
   (run `33875306007` corresponde a `9ba76f4`).
4. Toda futura modificación de código, workflow, tooling, manifiestos o artefactos invalida esta
   certificación hasta ejecutar de nuevo el proceso completo.
5. Una certificación posterior debe identificar explícitamente el nuevo source SHA y su run de CI exacto.
6. Los hashes SHA-256 de MSI/EXE/ZIP para este release se leen del `SHA256SUMS.txt` producido por
   CI run `33875306007`; este documento no los transcribe para no arrastrar hashes de otro build.

## 7. Dictamen Final

Bajo la regla:

`IMPLEMENTACIÓN REAL + TEST ADVERSARIAL + INTEGRACIÓN REAL + EVIDENCIA REPRODUCIBLE`

el **source commit** `9ba76f4593034632d59070b5bb73e9e4f99ff04d` queda certificado como **RELEASE PASS**:

- CI run `33875306007` verde en ambos jobs (Ubuntu spec + Windows desktop/WPF/WiX/certification pipeline),
  con `CI commit SHA == source SHA`.
- SEC-01 → SEC-11 corregidos, no evitables, auditados de forma independiente sobre este SHA.
- SEC-12 → SEC-20 intactos.
- 612/612 tests, build 0/0, `validate_package` 57/57, `conformance` 65/65, mutations 15/15,
  certification mutations 15/15, `release_gate` PASS (reproducido localmente y en CI).

**VERDICT: RELEASE PASS**
