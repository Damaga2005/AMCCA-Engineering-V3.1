# AUDIT / IMPLEMENTATION GAP MATRIX — AMCCA ENGINEERING V3.1

> **Status vocabulary:** `OPEN` | `IN_PROGRESS` | `BLOCKED` | `CLOSED`
> Protocol: AMCCA Engineering — Red Team Repair / Zero-Stub Completion Protocol

---

## Matriz de Defectos

| ID | Severidad | Especificación | Componente / Archivos | Estado |
|---|---|---|---|:---:|
| **DEF-001** | CRITICAL | `SPEC/08_POLICY_ENGINE.md` | `src/AMCCA.Core/Policy/PolicyEngine.cs` | CLOSED |
| **DEF-002** | HIGH | `SPEC/09_APPROVALS.md` | `src/AMCCA.Core/Policy/ApprovalManager.cs` | CLOSED |
| **DEF-003** | CRITICAL | `SPEC/09_APPROVALS.md` | `src/AMCCA.Core/Policy/ApprovalManager.cs` | CLOSED |
| **DEF-004** | HIGH | `SPEC/06_AGENT_SYSTEM.md`, `AGENTS.md` | `src/AMCCA.Core/Agents/AgentRuntime.cs` | CLOSED |
| **DEF-005** | HIGH | `SPEC/06_AGENT_SYSTEM.md`, `AGENTS.md` | `src/AMCCA.Core/Agents/AgentRuntime.cs` | CLOSED |
| **DEF-006** | CRITICAL | `SPEC/07_GATEWAY_PORT.md`, `SPEC/72_SECURITY_TESTS.md` | `src/AMCCA.Core/Gateway/*` | OPEN |
| **DEF-007** | HIGH | `SPEC/60_DESKTOP_UI.md` | `src/AMCCA.App/*` | OPEN |
| **DEF-008** | CRITICAL | `SPEC/13_DOMAIN_STATE_MACHINE.md`, `AGENTS.md` | `src/AMCCA.Core/Domain/ProductionService.cs` | OPEN |
| **DEF-009** | HIGH | `SPEC/44_PUBLISHING.md`, `SPEC/13_DOMAIN_STATE_MACHINE.md` | `src/AMCCA.Core/Domain/ProductionService.cs`, `src/AMCCA.Core/Publishing/*` | OPEN |
| **DEF-010** | HIGH | `SPEC/13_DOMAIN_STATE_MACHINE.md` | `src/AMCCA.Core/Domain/ProductionService.cs` | OPEN |
| **DEF-011** | CRITICAL | `SPEC/20_COST_ENGINE.md`, `DECISIONS.md (D-023)` | `src/AMCCA.Core/Policy/*`, `src/AMCCA.Core/Monetization/*` | OPEN |
| **DEF-012** | HIGH | `SPEC/50_SECURITY.md`, `SPEC/72_SECURITY_TESTS.md (S-11)` | `src/AMCCA.Core/Media/MediaRenderer.cs` | OPEN |
| **DEF-013** | HIGH | `SPEC/50_SECURITY.md`, `SPEC/72_SECURITY_TESTS.md (S-10)` | `src/AMCCA.Core/Security/SafeArchiveExtractor.cs` | OPEN |
| **DEF-014** | CRITICAL | `SPEC/28_RESEARCH_SOURCE_SECURITY.md`, `SPEC/72 (S-06, S-08)` | `src/AMCCA.Core/Security/SsrfValidator.cs` | OPEN |
| **DEF-015** | CRITICAL | `SPEC/03_DATABASE.md`, `DECISIONS.md (D-001)` | `src/AMCCA.Core/Database/Migrations/001_InitialSchema.sql` | OPEN |
| **DEF-016** | HIGH | `SPEC/15_JOBS_AND_LEASES.md` | `src/AMCCA.Core/Jobs/JobService.cs` | OPEN |
| **DEF-017** | HIGH | `SPEC/15_JOBS_AND_LEASES.md` | `src/AMCCA.Core/Jobs/JobService.cs` | OPEN |
| **DEF-018** | CRITICAL | `SPEC/03_DATABASE.md` | `src/AMCCA.Core/Database/Migrations/*`, tests | OPEN |
| **DEF-019** | HIGH | `.github/workflows/ci.yml` | `.github/workflows/ci.yml` | OPEN |
| **DEF-020** | HIGH | `BUILD_ORDER.md` | Repo root, commits | OPEN |
| **DEF-021** | HIGH | `SPEC/76_PACKAGING.md` | Packaging scripts, build | OPEN |
| **DEF-022** | MEDIUM | `SPEC/01_STACK_MANIFEST.md` | `src/AMCCA.Core/AMCCA.Core.csproj`, `src/AMCCA.App/AMCCA.App.csproj` | OPEN |
| **DEF-023** | HIGH | `SPEC/71_TEST_MATRIX.md` | `tests/*` | OPEN |
| **DEF-024** | MEDIUM | `SPEC/06_AGENT_SYSTEM.md` | `src/AMCCA.Core/Tools/ToolRegistry.cs` | OPEN |
| **DEF-025** | MEDIUM | `SPEC/13_DOMAIN_STATE_MACHINE.md` | `src/AMCCA.Core/Domain/StateMachineRegistry.cs` | OPEN |
| **DEF-026** | CRITICAL | `SPEC/03_DATABASE.md`, `SPEC/13_DOMAIN_STATE_MACHINE.md` | `src/AMCCA.Core/Domain/ProductionService.cs` | OPEN |

---

## Registro Detallado de Defectos

### DEF-001 — PolicyEngine Fail-Open
- **Severidad:** CRITICAL
- **Especificación incumplida:** `SPEC/08_POLICY_ENGINE.md`
- **Archivo(s):** `src/AMCCA.Core/Policy/PolicyEngine.cs`
- **Comportamiento actual:** `EvaluateAction` termina en `new PolicyDecisionResult("ALLOW", "policy.default_allow")` tras comprobar únicamente kill switches.
- **Por qué falla:** La especificación exige evaluación estricta y fail-closed de: Security, Safety, Rights, Compliance, Budget, Autonomy, Approval, Kill switches, Provider/Platform restrictions y Action-specific constraints. Ante cualquier incertidumbre o ausencia de regla explícita debe responder `DENY` o `REQUIRE_APPROVAL`, nunca `ALLOW` por defecto.
- **Test de regresión:** `tests/AMCCA.Core.Tests/PolicyEngineFailClosedRegressionTests.cs`
- **Fix:** `src/AMCCA.Core/Policy/PolicyEngine.cs` pipeline normativo completo (Missing Data -> Emergency Stop -> Security -> Safety -> Rights -> Compliance -> Provider -> Budget -> Human Approval -> Explicit Allow -> Fail-Closed Block).
- **Test ejecutado:** `dotnet test AMCCA.sln` (5 tests dedicados en suite)
- **Resultado:** PASS
- **Evidencia:** 314/314 tests pasando; ningún unknown o default allow permitido.
- **Estado:** CLOSED

### DEF-002 / DEF-003 — Approval Scope & Atomic Consumption
- **Severidad:** CRITICAL
- **Especificación incumplida:** `SPEC/09_APPROVALS.md`
- **Archivo(s):** `src/AMCCA.Core/Policy/ApprovalManager.cs`
- **Comportamiento actual:** `ValidateAndConsumeApprovalAsync` no verifica los campos de `scope_json` (exact target, subject, cost ceiling). Además no liga el consumo de forma indivisible a la ejecución de la acción protegida ante fallo o concurrencia.
- **Por qué falla:** Permite reutilizar aprobaciones para propósitos no acordados o carreras concurrentes.
- **Test de regresión:** `tests/AMCCA.Core.Tests/ApprovalScopeAndAtomicityRegressionTests.cs`
- **Fix:** `src/AMCCA.Core/Policy/ApprovalManager.cs` implementa `ExecuteWithApprovalAsync` con validación estricta de `ApprovalScope` (Target, Subject, CostCeiling) y consumo atómico transaccional con rollback en fallo y serialización ante concurrencia.
- **Test ejecutado:** `dotnet test AMCCA.sln` (5 tests de scope, límites, rollback y concurrencia multihilo)
- **Resultado:** PASS
- **Evidencia:** 319/319 tests pasando; concurrencia probada con 5 hilos simultáneos garantizando que sólo uno consume la aprobación de uso único.
- **Estado:** CLOSED

### DEF-004 / DEF-005 — Agent MaxCost & TimeoutSeconds Enforcement
- **Severidad:** HIGH
- **Especificación incumplida:** `SPEC/06_AGENT_SYSTEM.md`, `AGENTS.md`
- **Archivo(s):** `src/AMCCA.Core/Agents/AgentRuntime.cs`
- **Comportamiento actual:** `AgentContract` declara `MaxCost` y `TimeoutSeconds`, pero `AgentRuntime` no corta la ejecución con timeout de cancelación real ni bloquea tool calls si el costo acumulado excede `MaxCost`.
- **Por qué falla:** Los contratos no tienen enforcement en runtime.
- **Test de regresión:** `tests/AMCCA.Core.Tests/AgentContractEnforcementRegressionTests.cs`
- **Fix:** `src/AMCCA.Core/Agents/AgentRunSession.cs` y `src/AMCCA.Core/Agents/AgentRuntime.cs` con reserva thread-safe de presupuesto antes de ejecutar cualquier tool y cancelación estricta por `CancellationTokenSource.CancelAfter(TimeoutSeconds)`.
- **Test ejecutado:** `dotnet test AMCCA.sln` (5 tests de presupuesto exacto, exceso bloqueado, presupuesto acumulado, concurrencia de llamadas y timeout cancelado)
- **Resultado:** PASS
- **Evidencia:** 324/324 tests pasando; ningún tool call puede ejecutarse si excede el límite de costo ni extenderse más allá del timeout contratado.
- **Estado:** CLOSED

### DEF-006 — Real AI Provider Integration
- **Severidad:** CRITICAL
- **Especificación incumplida:** `SPEC/07_GATEWAY_PORT.md`, `SPEC/72_SECURITY_TESTS.md`
- **Archivo(s):** `src/AMCCA.Core/Gateway/OmniRoutersGatewayAdapter.cs`, `src/AMCCA.Core/Gateway/DirectOpenAiCompatibleGatewayAdapter.cs`
- **Comportamiento actual:** Los adaptadores comprueban formato de secret pero devuelven respuestas sintéticas sin invocar clientes HTTP tipados con serialización, timeout, parseo y manejo de errores reales.
- **Por qué falla:** Son stubs que sustituyen la integración real.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-007 — WPF Application Architecture
- **Severidad:** HIGH
- **Especificación incumplida:** `SPEC/60_DESKTOP_UI.md`
- **Archivo(s):** `src/AMCCA.App/`
- **Comportamiento actual:** `AMCCA.App` es una consola básica con `Program.cs`.
- **Por qué falla:** La especificación exige WPF con MVVM, Composition Root, Navigation y las pantallas Dashboard, Productions, Inspector, Job Queue, Approvals, Publications, Money, Evidence, Policies, Providers, Security, Safety, Settings, Diagnostics conectadas a la capa de aplicación.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-008 — Orchestrator Sole State Committer
- **Severidad:** CRITICAL
- **Especificación incumplida:** `SPEC/13_DOMAIN_STATE_MACHINE.md`, `AGENTS.md`
- **Archivo(s):** `src/AMCCA.Core/Domain/ProductionService.cs`
- **Comportamiento actual:** `ProductionService.TransitionAsync` comitea directamente a la base de datos sin pasar por un pipeline cerrado `Command -> Policy -> Orchestrator -> Transition -> Commit`.
- **Por qué falla:** La arquitectura exige que sólo el orquestador comitee cambios de estado de dominio.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-009 — UNKNOWN_EXTERNAL_STATE Enforcement
- **Severidad:** HIGH
- **Especificación incumplida:** `SPEC/44_PUBLISHING.md`, `SPEC/13_DOMAIN_STATE_MACHINE.md`
- **Archivo(s):** `src/AMCCA.Core/Domain/ProductionService.cs`, `src/AMCCA.Core/Publishing/*`
- **Comportamiento actual:** La transición desde `UNKNOWN_EXTERNAL_STATE` no está restringida a flujos de reconciliación autorizados.
- **Por qué falla:** Un caller genérico podría transicionar a `VERIFIED`.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-010 — BLOCKED Resume Authorization
- **Severidad:** HIGH
- **Especificación incumplida:** `SPEC/13_DOMAIN_STATE_MACHINE.md`
- **Archivo(s):** `src/AMCCA.Core/Domain/ProductionService.cs`
- **Comportamiento actual:** Se verifica la procedencia de `blocked_from`, pero no se valida que la condición que causó el bloqueo esté limpia y que exista una aprobación válida vigente.
- **Por qué falla:** Reanudar sin verificar limpieza o aprobación viola la seguridad de la máquina de estados.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-011 — Money Must Never Go Through Double
- **Severidad:** CRITICAL
- **Especificación incumplida:** `SPEC/20_COST_ENGINE.md`, `DECISIONS.md (D-023)`
- **Archivo(s):** `src/AMCCA.Core/Policy/BudgetManager.cs`, `src/AMCCA.Core/Monetization/RevenueService.cs`
- **Comportamiento actual:** Existen conversiones `(double)amount` y campos SQLite tratados como `REAL`.
- **Por qué falla:** El dinero NUNCA debe convertirse a coma flotante binaria (`double`/`float`). Debe representarse como `decimal` en memoria y cadena decimal canónica de 6 decimales en BD/JSON.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-012 — Path Confinement Beyond StartsWith
- **Severidad:** HIGH
- **Especificación incumplida:** `SPEC/50_SECURITY.md`, `SPEC/72_SECURITY_TESTS.md (S-11)`
- **Archivo(s):** `src/AMCCA.Core/Media/MediaRenderer.cs`
- **Comportamiento actual:** `StartsWith(root)` textual puede ser eludido con prefijos similares de hermanos (ej: `C:\data` vs `C:\database\evil`), case-insensitivity o separadores mixtos.
- **Por qué falla:** No asegura confinamiento estricto bajo el límite del directorio canónico.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-013 — Archive Extraction Limits (Bombs)
- **Severidad:** HIGH
- **Especificación incumplida:** `SPEC/50_SECURITY.md`, `SPEC/72_SECURITY_TESTS.md (S-10)`
- **Archivo(s):** `src/AMCCA.Core/Security/SafeArchiveExtractor.cs`
- **Comportamiento actual:** Valida path traversal pero no aplica límites sobre cantidad máxima de entradas, bytes totales descomprimidos o tamaño máximo individual.
- **Por qué falla:** Permite ataques de denegación de servicio por bombas de descompresión (zip bombs).
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-014 — SSRF + DNS Rebinding Protection
- **Severidad:** CRITICAL
- **Especificación incumplida:** `SPEC/28_RESEARCH_SOURCE_SECURITY.md`, `SPEC/72 (S-06, S-08)`
- **Archivo(s):** `src/AMCCA.Core/Security/SsrfValidator.cs`
- **Comportamiento actual:** Valida IP resolviendo una vez, pero no fija la conexión al socket con la IP validada, dejando una ventana TOCTOU ante DNS rebinding.
- **Por qué falla:** Un atacante puede responder con IP pública en la validación y privada en la conexión.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-015 — Events Append-Only Database Enforcement
- **Severidad:** CRITICAL
- **Especificación incumplida:** `SPEC/03_DATABASE.md`, `DECISIONS.md (D-001)`
- **Archivo(s):** `src/AMCCA.Core/Database/Migrations/001_InitialSchema.sql`
- **Comportamiento actual:** La inmutabilidad de la tabla `events` depende de que el código no ejecute UPDATE/DELETE. SQLite permite que cualquier conexión ejecute `UPDATE events` o `DELETE FROM events`.
- **Por qué falla:** La especificación exige que la base de datos impida físicamente mutaciones en `events` (vía triggers de abort/raise).
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-016 — Job Fail Fence Token Protection
- **Severidad:** HIGH
- **Especificación incumplida:** `SPEC/15_JOBS_AND_LEASES.md`
- **Archivo(s):** `src/AMCCA.Core/Jobs/JobService.cs`
- **Comportamiento actual:** `CompleteJobAsync` valida `fence_token`, pero `FailJobAsync` no comprueba que el worker siga teniendo la lease vigente y el fence token autorizado.
- **Por qué falla:** Un worker con lease expirada podría marcar como fallido un job reasignado a otro worker activo.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-017 — Expired Lease Heartbeat Renewal
- **Severidad:** HIGH
- **Especificación incumplida:** `SPEC/15_JOBS_AND_LEASES.md`
- **Archivo(s):** `src/AMCCA.Core/Jobs/JobService.cs`
- **Comportamiento actual:** `RenewLeaseAsync` no valida explícitamente si `expires_at` ya venció en el momento del heartbeat.
- **Por qué falla:** Si la lease expiró, el heartbeat debe ser denegado.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-018 — Migrations Must Be The Real Schema
- **Severidad:** CRITICAL
- **Especificación incumplida:** `SPEC/03_DATABASE.md`
- **Archivo(s):** `src/AMCCA.Core/Database/Migrations/*`, suites de tests
- **Comportamiento actual:** En tests anteriores, se creaban tablas manualmente (`CREATE TABLE IF NOT EXISTS...`) dentro del setup de los tests en lugar de ejecutar la cadena formal de migraciones de `MigrationService`.
- **Por qué falla:** Una base de datos de producción limpia falla si las migraciones oficiales no contienen todas las tablas y restricciones probadas en tests.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-019 — Real .NET CI Execution
- **Severidad:** HIGH
- **Especificación incumplida:** `SPEC/02_CI_CD.md`
- **Archivo(s):** `.github/workflows/ci.yml`
- **Comportamiento actual:** El CI sólo ejecuta validadores Python y no compila ni ejecuta la solución y tests .NET.
- **Por qué falla:** El CI no garantiza la salud del código de producción ni de los tests.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-020 — Reconciliation of BUILD_ORDER.md
- **Severidad:** HIGH
- **Especificación incumplida:** `BUILD_ORDER.md`
- **Archivo(s):** Historial y estructura del proyecto
- **Comportamiento actual:** Discrepancia entre la numeración de 18 fases de `BUILD_ORDER.md` y los commits realizados en el ciclo inicial.
- **Por qué falla:** La autoridad del orden es `BUILD_ORDER.md`.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-021 — Real Packaging Verification
- **Severidad:** HIGH
- **Especificación incumplida:** `SPEC/76_PACKAGING.md`
- **Archivo(s):** Packaging configuration
- **Comportamiento actual:** Solo se compiló un binario self-contained sin script de verificación de instalación/MSI o dependencias.
- **Por qué falla:** La especificación exige validación de empaquetado verificable.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-022 — Mandatory Architectural Dependencies
- **Severidad:** MEDIUM
- **Especificación incumplida:** `SPEC/01_STACK_MANIFEST.md`
- **Archivo(s):** `src/AMCCA.Core/AMCCA.Core.csproj`, `src/AMCCA.App/AMCCA.App.csproj`
- **Comportamiento actual:** Ausencia de dependencias normativas como `HttpClientFactory`, `Polly`, `Serilog` integradas activamente en los proyectos de código.
- **Por qué falla:** La arquitectura exige resiliencia con Polly, logging estructurado con Serilog y gestión de conexiones HTTP con `IHttpClientFactory`.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-023 — Comprehensive Test Pyramid
- **Severidad:** HIGH
- **Especificación incumplida:** `SPEC/71_TEST_MATRIX.md`
- **Archivo(s):** `tests/*`
- **Comportamiento actual:** Tests concentrados en contratos unitarios básicos, requiriendo cobertura de integración de extremo a extremo, concurrencia y seguridad.
- **Por qué falla:** La pirámide de pruebas exige capas explícitas probando comportamiento real y no asserts triviales.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-024 — ToolRegistry Duplicate Registration
- **Severidad:** MEDIUM
- **Especificación incumplida:** `SPEC/06_AGENT_SYSTEM.md`
- **Archivo(s):** `src/AMCCA.Core/Tools/ToolRegistry.cs`
- **Comportamiento actual:** Registro de tool con nombre repetido sobrescribe o no lanza excepción explícita.
- **Por qué falla:** La especificación prohíbe sobrescrituras silenciosas de tools.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-025 — StateMachineRegistry Construction Validation
- **Severidad:** MEDIUM
- **Especificación incumplida:** `SPEC/13_DOMAIN_STATE_MACHINE.md`
- **Archivo(s):** `src/AMCCA.Core/Domain/StateMachineRegistry.cs`
- **Comportamiento actual:** Permite registrar transiciones sin validar duplicidad o estados inalcanzables.
- **Por qué falla:** La especificación exige construcción estricta de la máquina de estados.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN

### DEF-026 — Event / State Atomic Transaction
- **Severidad:** CRITICAL
- **Especificación incumplida:** `SPEC/03_DATABASE.md`, `SPEC/13_DOMAIN_STATE_MACHINE.md`
- **Archivo(s):** `src/AMCCA.Core/Domain/ProductionService.cs`
- **Comportamiento actual:** La mutación de estado y la inserción de evento deben garantizarse bajo la misma transacción atómica en SQLite, asegurando que ante cualquier excepción no exista estado cambiado sin evento o evento sin cambio de estado.
- **Por qué falla:** Un fallo parcial corrompe la trazabilidad del event sourcing.
- **Test de regresión:** Pendiente
- **Fix:** Pendiente
- **Test ejecutado:** Pendiente
- **Resultado:** Pendiente
- **Evidencia:** Pendiente
- **Estado:** OPEN
