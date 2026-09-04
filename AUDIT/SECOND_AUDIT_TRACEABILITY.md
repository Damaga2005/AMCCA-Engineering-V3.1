# AMCCA Engineering V3.1 — Second Forensic Audit: Traceability Matrix

> **Modo:** RED TEAM / RELEASE BLOCKER / ZERO TRUST  
> **Fecha:** 2026-09-03  
> **Commit Auditado:** `d1d9fefe32be33fc0b34130598cb535a3bc6e398`  

---

| Requirement | Implementation | Test | CI | Evidence | Status |
|---|---|---|---|---|---|
| **I-01** (One committed state) | `ProductionService.cs`, `StateMachineRegistry.cs` | `StateMachineContractTests.cs` | `.github/workflows/validation.yml` | Transición atómica en SQLite con verificación de `aggregate_version`. | **PASS** |
| **I-02** (Event per transition) | `ProductionService.TransitionAsync` | `ArchitectureAndRegistryRegressionTests.cs` | `.github/workflows/validation.yml` | Fallo forzado en `events` revierte transacción completa TX-1. | **PASS** |
| **I-03** (Intent before effect) | `JobManager.cs` | `JobsAndLeasesContractTests.cs` | `.github/workflows/validation.yml` | Registro en `intents` previo a cualquier llamada externa. | **PASS** |
| **I-04** (Unknown stays unknown) | `JobManager.cs` | `JobsAndLeasesContractTests.cs` | `.github/workflows/validation.yml` | Timeout tras envío marca `UNKNOWN`; reintento sin reconciliación bloqueado. | **PASS** |
| **I-05** (Single active lease & fence token) | `JobManager.cs` | `JobLeaseFenceAndHeartbeatRegressionTests.cs` | `.github/workflows/validation.yml` | Reclamación concurrente atómica; `CompleteJob` y `FailJob` rechazan tokens obsoletos (`AMCCA-JOB-003`). | **PASS** |
| **I-06** (Budget not exceeded) | `BudgetManager.cs` | `AgentContractEnforcementRegressionTests.cs` | `.github/workflows/validation.yml` | N reservas concurrentes contra N-1 capacidad; rechaza exceso (`AMCCA-BUD-002`). | **PASS** |
| **I-07** (Sealed manifests immutable) | `MigrationService.cs` (triggers) | `CanonicalMigrationSchemaTests.cs` | `.github/workflows/validation.yml` | Esquema formal incluye tablas de manifiestos versionados inmutables. | **PASS** |
| **I-08** (Tombstones auditable) | `ArtifactDag.cs` | `QaEngineAndDagReworkContractTests.cs` | `.github/workflows/validation.yml` | Nodos invalidados pasan a `INVALIDATED` sin eliminarse de la estructura. | **PASS** |
| **I-09** (Agents cannot mutate persistent state) | `ProductionService.cs`, `AgentRuntime.cs` | `OrchestratorAndStateResumeRegressionTests.cs` | `.github/workflows/validation.yml` | Solo `Orchestrator` y `HUMAN` pueden transicionar estado; actor `AGENT` ausente de auditoría. | **PASS** |
| **I-10** (Policy block terminal without change) | `PolicyEngine.cs` | `PolicyEngineFailClosedRegressionTests.cs` | `.github/workflows/validation.yml` | Reintento de acción bloqueada sin cambio de política genera el mismo bloqueo. | **PASS** |
| **I-11** (Verified needs evidence) | `PlatformHub.cs`, DDL checks | `PublishingAndPlatformHubContractTests.cs` | `.github/workflows/validation.yml` | CHECK constraint en `publications` exige `evidence_source` si estado es `VERIFIED`. | **PASS** |
| **I-12** (Estimate does not overwrite measurement) | `RevenueService.cs` | `MonetizationAndRevenueContractTests.cs` | `.github/workflows/validation.yml` | Lectura de ventana prioriza mediciones reales sobre estimaciones. | **PASS** |
| **I-13** (Estimates out of revenue ledger) | DDL constraint en `revenue_events` | `MonetizationAndRevenueContractTests.cs` | `.github/workflows/validation.yml` | CHECK constraint `provenance != 'ESTIMATED'` bloquea inserciones espurias. | **PASS** |
| **I-14** (No secrets leak in logs/diagnostics) | `SecretStore.cs`, `AiProviderRealIntegrationTests.cs` | `AiProviderRealIntegrationTests.cs` | `.github/workflows/validation.yml` | Secretos no se imprimen en logs ni excepciones. | **PASS** |
| **I-15** (No self-elevation by agents) | `AgentRuntime.cs` | `AgentContractEnforcementRegressionTests.cs` | `.github/workflows/validation.yml` | Agente intentando elevar su autonomía o presupuesto es rechazado y auditado. | **PASS** |
| **I-16** (Emergency stop persists across restarts) | `OperatorService.cs` | `ConfigAndPreflightContractTests.cs` | `.github/workflows/validation.yml` | Activación de kill switch sobrevive a reinicio de proceso. | **PASS** |
| **I-17** (No duplicate publication) | `PlatformHub.cs`, SQLite unique constraint | `PublishingAndPlatformHubContractTests.cs` | `.github/workflows/validation.yml` | Constraint única en `publications(production_id, platform, account_id)` impide duplicados. | **PASS** |
| **I-18** (No unlabelled synthetic content) | `ComplianceGate.cs` | `MediaPipelineAndDisclosureContractTests.cs` | `.github/workflows/validation.yml` | Contenido sintético sin declaración requerida arroja `AMCCA-CMP-001`. | **PASS** |
| **I-19** (No AI-only QA PASS) | `QaVerdictEvaluator.cs` | `QaEngineAndDagReworkContractTests.cs` | `.github/workflows/validation.yml` | Evaluación de QA sin comprobaciones deterministas arroja `AMCCA-QA-002`. | **PASS** |
| **I-20** (State machine well-formed) | `SCHEMAS/state-machine.json` | `TOOLS/validate_package.py` | `.github/workflows/validation.yml` | 18 checks de máquina de estados pasando 100%. | **PASS** |
| **I-21** (Every table has a contract) | `SCHEMAS/tables.json` | `TOOLS/validate_package.py` | `.github/workflows/validation.yml` | Todas las tablas de base de datos poseen contrato canónico. | **PASS** |
| **I-22** (No network call inside transaction) | DDL / Service design | Suite general de tests | `.github/workflows/validation.yml` | Arquitectura desacopla transacciones locales de llamadas de red. | **PASS** |
| **SPEC/15 / SPEC/16** (Crash recovery kill -9 checkpoints) | `JobManager.cs`, `RecoveryService.cs` | **INCOMPLETO** | `.github/workflows/validation.yml` | No existen tests que maten procesos reales (SIGKILL / kill -9) y verifiquen recuperación tras rearranque. | **FAIL** |
| **SPEC/28** (Coupled DNS SSRF prevention) | `SsrfValidator.CreateSafeSocketsHttpHandler` | `SsrfAndDnsRebindingRegressionTests.cs` | `.github/workflows/validation.yml` | **DESCONECTADO EN PRODUCCIÓN.** Ningún cliente HTTP de scraping o fetch consume este handler. | **FAIL** |
| **SPEC/37** (Dynamic DAG Rework resolution) | `ArtifactDag.cs` | `QaEngineAndDagReworkContractTests.cs` | `.github/workflows/validation.yml` | `DagReworkResolver` no calcula subárboles de invalidación en base a fallos de QA. | **FAIL** |
| **SPEC/40 / SPEC/44** (Platform adapters & OAuth) | `PlatformHub.cs` | `PublishingAndPlatformHubContractTests.cs` | `.github/workflows/validation.yml` | No existen adaptadores reales hacia YouTube/TikTok ni gestión de OAuth. | **FAIL** |
| **SPEC/60 / SPEC/61** (Memory, Genome, Niche to PROVEN) | **NO IMPLEMENTADO** | **NO IMPLEMENTADO** | `.github/workflows/validation.yml` | Fase 15 de `BUILD_ORDER.md` totalmente ausente en código C#. | **FAIL** |
| **SPEC/65 / SPEC/66** (WPF Desktop UI & Inspectors) | **NO IMPLEMENTADO** | **NO IMPLEMENTADO** | `.github/workflows/validation.yml` | `AMCCA.App` es una consola sin XAML, ni MVVM, ni pantallas de operador. | **FAIL** |
| **SPEC/73** (Concurrency test suite C-01 a C-14) | Parcial en varios tests | **SUITE AUSENTE** | `.github/workflows/validation.yml` | Escenarios C-01 a C-14 no están agrupados ni ejecutados como suite formal. | **FAIL** |
| **SPEC/74** (Chaos test suite X-01 a X-16) | Parcial en varios tests | **SUITE AUSENTE** | `.github/workflows/validation.yml` | Escenarios X-01 a X-16 no existen como suite formal auditable. | **FAIL** |
| **SPEC/76** (WiX MSI Installer `AMCCA-Setup.exe`) | **NO IMPLEMENTADO** | `PackagingVerificationRegressionTests.cs` | `.github/workflows/validation.yml` | Solo se compila binario por `dotnet publish`. No existe empaquetador WiX ni MSI. | **FAIL** |
