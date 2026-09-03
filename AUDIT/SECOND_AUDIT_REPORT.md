# AMCCA Engineering V3.1 — Second Independent Forensic Audit Report

> **MODO:** RED TEAM / RELEASE BLOCKER / ZERO TRUST  
> **FECHA DE AUDITORÍA:** 2026-09-03  
> **NORMA DE AUDITORÍA:** `BUILD_ORDER.md` y `SPEC/**`  

---

## 1. Repository Identity

- **Repository:** `Damaga2005/AMCCA-Engineering-V3.1`
- **Branch:** `add_amcca_engineering_repo`
- **HEAD Commit SHA:** `d1d9fefe32be33fc0b34130598cb535a3bc6e398`
- **Remote SHA Match:** `d1d9fefe32be33fc0b34130598cb535a3bc6e398` (VERIFIED — Identidad exacta con remoto `origin`)
- **Working Tree State:** Clean (sin modificaciones no commiteadas)
- **Tracked File Count:** 271 archivos
- **Csproj Count:** 3 proyectos
  - `src/AMCCA.Core/AMCCA.Core.csproj`
  - `src/AMCCA.App/AMCCA.App.csproj`
  - `tests/AMCCA.Core.Tests/AMCCA.Core.Tests.csproj`
- **Solution File:** `AMCCA.sln`
- **CI Workflow File:** `.github/workflows/validation.yml`

---

## 2. Executive Verdict

# **RELEASE VERDICT: FAIL**

Bajo la regla de tolerancia cero de la Sección 41 del protocolo de auditoría:
- **CRITICAL findings:** 4
- **HIGH findings:** 5
- **MEDIUM / LOW findings:** 3
- **BUILD_ORDER divergences:** 3
- **Fases aprobadas:** 9 / 18
- **Fases con fallo:** 9 / 18

El sistema **NO es apto para release** en su estado actual.

---

## 3. Phase Matrix (18 Fases de BUILD_ORDER)

| Fase | Nombre de Fase | Estado | Razón Principal del Fallo / Cumplimiento |
|---|---|---|---|
| 1 | Repository, CI, package validator | **FAIL** | Runner configurado solo en `ubuntu-latest` (incapaz de validar UI Windows) y ejecuciones de CI no verificables externamente sin credenciales. |
| 2 | Configuration, secrets, preflight | **PASS** | `ConfigManager` y `PreflightService` ejecutan validación fail-closed y rechazan secretos en claro. |
| 3 | Database, migrations, event store | **FAIL** | 9 suites de pruebas unitarias crean tablas manualmente con DDL inline, eludiendo la migración canónica. |
| 4 | Domain model and state machine | **PASS** | `StateMachineRegistry` valida 100% de transiciones normativas; transiciones no listadas rechazadas; TX-1 atómica. |
| 5 | Jobs, leases, idempotency, recovery | **PASS** | Fence tokens verificados en fallo/completado; leases atómicas; `UNKNOWN` protegido. |
| 6 | Tool registry and agent runtime | **PASS** | Restricciones de herramientas prohibidas, costos máximos y timeouts ejecutados rígidamente. |
| 7 | Provider gateway & adapters | **FAIL** | Tests de integración utilizan `ControlledHttpMessageHandler` en memoria en lugar de endpoints de red/loopback reales. |
| 8 | Research, claims, sources | **FAIL** | El handler anti-SSRF acoplado a DNS (`CreateSafeSocketsHttpHandler`) está desconectado del código de scraping. |
| 9 | Script, storyboard, assets, render | **PASS** | Confinamiento de rutas de renderizado, hashing determinista y trazabilidad de artefactos. |
| 10 | Deterministic QA, rights, duplicates | **PASS** | Regla I-19 y D-024 cumplida: veredicto PASS inalcanzable con hallazgos de IA únicamente (`AMCCA-QA-002`). |
| 11 | Rework and DAG invalidation | **FAIL** | `DagReworkResolver` es un contador numérico aislado que no mapea fallos de QA ni calcula la invalidación del DAG en runtime. |
| 12 | Platform hub, OAuth, publishing | **FAIL** | `PlatformHub.cs` solo realiza inserciones en DB; adaptadores reales de plataforma y flujo OAuth inexistentes. |
| 13 | Synthetic-content disclosure | **PASS** | Bloqueo estricto `AMCCA-CMP-001` ante omisión de etiquetas sintéticas obligatorias y C2PA. |
| 14 | Monetization & revenue | **PASS** | Aritmética decimal estricta en todo el flujo monetario; estimaciones excluidas físicamente del ledger. |
| 15 | Memory, genome, experiments | **FAIL** | **INEXISTENTE EN CÓDIGO.** No hay clases ni servicios para nichos, genomas ni evaluación de métricas hacia `PROVEN`. |
| 16 | Desktop UI and inspectors | **FAIL** | **INEXISTENTE COMO WPF.** `AMCCA.App` es una consola básica (`Program.cs`) sin XAML, vistas, ViewModels ni MVVM. |
| 17 | Chaos, concurrency, security | **FAIL** | Las suites de SPEC/73 (C-01 a C-14) y SPEC/74 (X-01 a X-16) no existen como suites formales ejecutadas. |
| 18 | Packaging, installer, signing | **FAIL** | No existe instalador WiX MSI (`AMCCA-Setup.exe`) ni archivos `.wxs`; solo se genera binario de consola. |

---

## 4. Critical Findings (Severidad: CRITICAL)

### AUDIT-002 — Ausencia Total de Desktop UI WPF (Fase 16)
- **Severidad:** **CRITICAL** (Release Blocker)
- **Especificación incumplida:** `SPEC/65_OPERATOR_INTERFACE.md`, `SPEC/66_INSPECTOR_VIEWS.md`, `BUILD_ORDER.md` Fase 16.
- **Ubicación:** `src/AMCCA.App/AMCCA.App.csproj`, `src/AMCCA.App/Program.cs`
- **Comportamiento actual:** `AMCCA.App` es una aplicación de consola rudimentaria con un método `Main` que solo escribe 2 líneas con `Console.WriteLine` y finaliza.
- **Comportamiento esperado:** Aplicación de escritorio WPF auténtica con arquitectura MVVM, vistas de Dashboard, Producciones, Cola de Jobs, Aprobaciones, Publicaciones, Dinero, Evidencias, Políticas, Seguridad y Diagnósticos.
- **Impacto:** Incumplimiento del 100% de la Fase 16. Imposibilidad de que un operador humano supervise el sistema.

### AUDIT-003 — Omisión Absoluta de la Fase 15 (Memory / Genome / Experiments)
- **Severidad:** **CRITICAL** (Release Blocker)
- **Especificación incumplida:** `SPEC/60_NICHE_GENOME.md`, `SPEC/61_EXPERIMENT_ENGINE.md`, `BUILD_ORDER.md` Fase 15.
- **Ubicación:** `src/AMCCA.Core/`
- **Comportamiento actual:** No existe ningún servicio, modelo ni lógica implementada en C# para la evolución de genomas o experimentos. La palabra `PROVEN` solo figura en el script SQL de migraciones.
- **Comportamiento esperado:** Un motor de experimentos y aprendizaje que promueva nichos al estado `PROVEN` basándose estrictamente en resultados medidos.
- **Impacto:** Incumplimiento íntegro de la Fase 15.

### AUDIT-004 — Creación Manual de Tablas en Suites de Tests (Fase 3 / Calidad de Tests)
- **Severidad:** **CRITICAL**
- **Especificación incumplida:** `SPEC/03_DATABASE.md`, Sección 8 del Protocolo de Auditoría.
- **Ubicación:** 9 archivos en `tests/AMCCA.Core.Tests/`:
  - `ContentPipelineAndPromptContractTests.cs` (Líneas 34-54)
  - `JobsAndLeasesContractTests.cs` (Líneas 31-71)
  - `MonetizationAndRevenueContractTests.cs` (Líneas 31-48)
  - `OperatorControlAndAuditContractTests.cs` (Líneas 42-56)
  - `ProviderGatewayAndModelRegistryContractTests.cs` (Línea 31)
  - `PolicyBudgetAndApprovalContractTests.cs` (Líneas 33-59)
  - `StateMachineContractTests.cs` (Líneas 170-188)
  - `ResearchAndClaimValidationContractTests.cs` (Líneas 32-57)
  - `PublishingAndPlatformHubContractTests.cs` (Líneas 31-41)
- **Comportamiento actual:** Los tests ejecutan DDL inline manual (`CREATE TABLE IF NOT EXISTS`) con esquemas parciales, esquivando la migración canónica.
- **Comportamiento esperado:** Todo test de base de datos debe inicializar una base de datos vacía y aplicar las migraciones oficiales mediante `MigrationService.UpgradeAsync()`.
- **Impacto:** Falsa sensación de cobertura; los tests pueden pasar mientras el esquema de producción real está desalineado.

### AUDIT-005 — Handler Anti-SSRF Desconectado en Código de Producción (Fase 8 / Seguridad)
- **Severidad:** **CRITICAL**
- **Especificación incumplida:** `SPEC/28_RESEARCH_SOURCE_SECURITY.md`, `SPEC/72_SECURITY_TESTS.md (S-06, S-08)`.
- **Ubicación:** `src/AMCCA.Core/Security/SsrfValidator.cs` (Línea 152) y `src/AMCCA.Core/Research/ResearchService.cs`
- **Comportamiento actual:** `CreateSafeSocketsHttpHandler()` implementa la validación de sockets acoplada a DNS, pero **ningún componente de producción lo consume**. Las llamadas externas operan desprotegidas contra DNS Rebinding dinámico en tiempo de conexión.
- **Comportamiento esperado:** Todo cliente HTTP utilizado para la ingesta de fuentes o scraping debe construirse obligatoriamente con el handler seguro acoplado.
- **Impacto:** Vulnerabilidad potencial de SSRF y TOCTOU de DNS en entornos de producción.

---

## 5. High Findings (Severidad: HIGH)

### AUDIT-006 — Inexistencia de Adaptadores Reales de Plataforma y OAuth (Fase 12)
- **Severidad:** **HIGH**
- **Especificación incumplida:** `SPEC/40_PLATFORM_INTEGRATIONS.md`, `SPEC/44_PUBLISHING.md`.
- **Ubicación:** `src/AMCCA.Core/Publishing/PlatformHub.cs`
- **Comportamiento actual:** `PlatformHub` únicamente registra filas en SQLite. No existen adaptadores para YouTube, TikTok o Instagram, ni soporte para renovación de tokens OAuth.
- **Comportamiento esperado:** Adaptadores de plataforma tipados que gestionen el ciclo de vida de OAuth, envíos con reintentos controlados y captura de identificadores externos autoritativos.

### AUDIT-007 — DagReworkResolver No Calcula Invalidaciones en Runtime (Fase 11)
- **Severidad:** **HIGH**
- **Especificación incumplida:** `SPEC/37_REWORK_AND_INVALIDATION.md`.
- **Ubicación:** `src/AMCCA.Core/QA/ArtifactDag.cs` (Líneas 73-87)
- **Comportamiento actual:** `DagReworkResolver` es un contador simple (`currentAttempts < _maxReworkAttempts`) que no interactúa con los grafos de artefactos para marcar invalidaciones ante fallos de QA.
- **Comportamiento esperado:** Resolución automatizada que, dado un fallo de QA en un artefacto, calcule el subárbol descendiente en el DAG y transicione su estado a `INVALIDATED`.

### AUDIT-008 — Ausencia de Suites Dedicadas de Caos (SPEC/74) y Concurrencia (SPEC/73) (Fase 17)
- **Severidad:** **HIGH**
- **Especificación incumplida:** `SPEC/73_CONCURRENCY_TESTS.md`, `SPEC/74_CHAOS_TESTS.md`.
- **Ubicación:** `tests/AMCCA.Core.Tests/`
- **Comportamiento actual:** Los escenarios formales C-01 a C-14 y X-01 a X-16 no están estructurados ni ejecutados como suites de prueba dedicadas con inyección de fallos por caída de proceso (`kill -9`).
- **Comportamiento esperado:** Suites formales que demuestren recuperación ante caídas abruptas en cada uno de los checkpoints de SPEC/74.

### AUDIT-009 — Ausencia de Instalador WiX MSI (`AMCCA-Setup.exe`) (Fase 18)
- **Severidad:** **HIGH**
- **Especificación incumplida:** `SPEC/76_PACKAGING.md`.
- **Ubicación:** Raíz del repositorio / `src/`
- **Comportamiento actual:** Solo se dispone de configuración para generar un binario self-contained mediante `dotnet publish`. No existen archivos de definición WiX (`.wxs`) ni pipeline para generar `AMCCA-Setup.exe`.
- **Comportamiento esperado:** Paquete de instalación MSI verificable con soporte de clean install, upgrade y uninstall conservando datos de usuario.

### AUDIT-010 — Tests de Proveedor AI No Prueban Sockets de Red Reales (Fase 7)
- **Severidad:** **HIGH**
- **Especificación incumplida:** `SPEC/08_AI_GATEWAY.md`, Sección 12 del Protocolo de Auditoría.
- **Ubicación:** `tests/AMCCA.Core.Tests/AiProviderRealIntegrationTests.cs`
- **Comportamiento actual:** Los tests de integración utilizan `ControlledHttpMessageHandler` interceptando llamadas en memoria, sin realizar conexiones reales sobre sockets TCP/loopback.
- **Comportamiento esperado:** Verificación de comportamiento HTTP completo mediante un servidor mock local (por ejemplo `TestServer` o `HttpListener` sobre loopback).

---

## 6. Medium / Low Findings (Severidad: MEDIUM / LOW)

### AUDIT-011 — Incompatibilidad del Runner de CI con Aplicaciones Windows Desktop (Fase 1)
- **Severidad:** **MEDIUM**
- **Ubicación:** `.github/workflows/validation.yml` (Línea 10: `runs-on: ubuntu-latest`)
- **Descripción:** Cuando se incorpore la Desktop UI en WPF (`net8.0-windows`), el runner de Ubuntu fallará al compilar proyectos que requieran el SDK de Windows Desktop. Se requiere migración a `windows-latest` o ejecución condicional.

### AUDIT-012 — Inexistencia de Verificación de Ejecución Remota de CI en Entorno Local (Fase 1)
- **Severidad:** **LOW**
- **Ubicación:** Entorno de auditoría local
- **Descripción:** La máquina local no cuenta con GitHub CLI (`gh`) ni tokens de API para consultar el historial de ejecuciones de GitHub Actions del repositorio remoto.

---

## 7. Stub Report

Consulte el informe dedicado en [AUDIT/SECOND_AUDIT_STUB_REPORT.md](file:///C:/Users/dmart/.gemini/antigravity/worktrees/Automatizaci%C3%B3n/add_amcca_engineering_repo/AUDIT/SECOND_AUDIT_STUB_REPORT.md).
- **Stubs crudos (`TODO`, `NotImplementedException`):** 0 encontrados.
- **Stubs semánticos de producción detectados:** 4 hallazgos críticos/altos (`Program.cs` de consola, `CreateSafeSocketsHttpHandler` desconectado, adaptadores de plataforma no implementados, `DagReworkResolver` incompleto).

---

## 8. Test Quality Assessment

- **Total de pruebas ejecutadas:** 385 tests (385 superados en Release).
- **Evaluación crítica:**
  - Los tests existentes para transiciones de estado, hashing determinista, parsing monetario, triggers de inmutabilidad y contratos de agentes son sólidos y de alta calidad.
  - **Defecto grave de diseño:** 9 suites de pruebas unitarias crean sus propias tablas SQLite manualmente con DDL inline, ocultando posibles divergencias entre el código de producción y las migraciones oficiales.
  - **Prueba adversarial de sustitución por stub:** Si se sustituyera el cliente HTTP o la capa de plataforma por stubs que siempre retornen éxito, los tests actuales de publicación y de investigación seguirían pasando porque no validan la red externa real.

---

## 9. Security Assessment

- **SSRF:** Reglas de validación de IP privada/reservada completas en `SsrfValidator.cs`. Sin embargo, el handler seguro acoplado a DNS no está conectado a los flujos de producción.
- **Confinamiento de Archivos:** `SafeArchiveExtractor.cs` previene path traversal, symlinks maliciosos y bombas de compresión (`ratio > 100`). `MediaRenderer` confina las rutas de salida bajo el directorio raíz.
- **Inmutabilidad de Eventos:** Validada a nivel de motor de base de datos mediante triggers físicos de SQLite que lanzan `ABORT` ante cualquier sentencia `UPDATE` o `DELETE` sobre `events` y `audit_log`.
- **Aprobaciones de Operador:** Protegidas con exclusión mutua (`SemaphoreSlim`), comprobación de scopes y consumo atómico de un solo uso.

---

## 10. Migration Assessment

- **Migraciones implementadas:** 3 migraciones canónicas (`001_initial_schema`, `002_append_only_triggers`, `003_complete_canonical_schema`).
- **Cobertura de tablas:** 58 de las 58 tablas declaradas en `SCHEMAS/tables.json` están presentes en `003_complete_canonical_schema`.
- **Rollback y Restore:** Probados con éxito en `CanonicalMigrationSchemaTests.cs`.
- **Brecha identificada:** Las pruebas unitarias del repositorio no reutilizan estas migraciones de manera uniforme, creando esquemas aislados ad-hoc.

---

## 11. UI Assessment

- **Diagnóstico:** **FAIL CRÍTICO.**
- `src/AMCCA.App` no contiene ningún archivo XAML, ningún ViewModel, ninguna clase de navegación ni vistas de operador. Es exclusivamente un ejecutable de consola que imprime un mensaje estático y finaliza.
- Incumple de forma flagrante los requerimientos de `SPEC/65` y `SPEC/66`.

---

## 12. Packaging Assessment

- **Diagnóstico:** **FAIL.**
- `dotnet publish` compila exitosamente un binario self-contained `win-x64` para consola.
- No se genera el instalador WiX MSI (`AMCCA-Setup.exe`).
- No existen procedimientos ni scripts de prueba para clean install, upgrade ni uninstall.

---

## 13. BUILD_ORDER Divergences

1. **Fase 15 no implementada:** Se avanzó en la numeración de fases sin haber construido el motor de memoria y genoma.
2. **Fase 16 sustituida por consola:** Se reportó como concluida una fase de UI sin haber desarrollado la interfaz gráfica WPF.
3. **Fase 17 y 18 incompletas:** Se ejecutaron validaciones parciales pero no las suites integrales de caos (kill -9) ni el instalador WiX.

---

## 14. Traceability Gaps

Consulte la matriz completa en [AUDIT/SECOND_AUDIT_TRACEABILITY.md](file:///C:/Users/dmart/.gemini/antigravity/worktrees/Automatizaci%C3%B3n/add_amcca_engineering_repo/AUDIT/SECOND_AUDIT_TRACEABILITY.md).
- Requisitos sin implementación: `SPEC/60`, `SPEC/61`, `SPEC/65`, `SPEC/66`, `SPEC/76` (WiX).
- Requisitos con suites de prueba faltantes: `SPEC/73` (C-01 a C-14), `SPEC/74` (X-01 a X-16).

---

## 15. Required Fixes (Catálogo de Correcciones para Fase de Reparación)

| AUDIT-ID | Ubicación Exacta | Causa del Defecto | Comportamiento Esperado | Test Requerido | Corrección Requerida |
|---|---|---|---|---|---|
| **AUDIT-002** | `src/AMCCA.App/` | Proyecto es una consola sin XAML. | Aplicación WPF completa con vistas de Dashboard, Inspección y Aprobación. | Tests de instanciación de ViewModels y bindings. | Convertir `AMCCA.App` a WPF (`net8.0-windows`), crear vistas XAML y ViewModels MVVM. |
| **AUDIT-003** | `src/AMCCA.Core/` | Fase 15 omitida en código C#. | Servicio de genomas y experimentos que evalúe métricas para alcanzar `PROVEN`. | Test unitario y de integración de nicho a `PROVEN`. | Implementar `GenomeService.cs` y `ExperimentEngine.cs`. |
| **AUDIT-004** | `tests/AMCCA.Core.Tests/*.cs` (9 archivos) | DDL inline en tests eludiendo migraciones. | Todos los tests deben usar `MigrationService.UpgradeAsync()`. | Ejecutar suite completa con DB generada puramente por migraciones. | Refactorizar los 9 archivos de test para eliminar `CREATE TABLE` manuales. |
| **AUDIT-005** | `src/AMCCA.Core/Research/` | Handler anti-SSRF desconectado de producción. | Cliente de fetch de fuentes debe usar `CreateSafeSocketsHttpHandler()`. | Test de intento de conexión SSRF contra handler activo. | Inyectar `CreateSafeSocketsHttpHandler()` en el `HttpClient` de investigación. |
| **AUDIT-006** | `src/AMCCA.Core/Publishing/` | Sin adaptadores reales ni OAuth. | Adaptadores para YouTube/TikTok y flujo OAuth estructurado. | Test de adaptador con respuestas mockeadas de API externa. | Crear adaptadores de plataforma e interfaz OAuth. |
| **AUDIT-007** | `src/AMCCA.Core/QA/ArtifactDag.cs` | `DagReworkResolver` es un contador aislado. | Mapeo de fallos QA hacia subgrafos de invalidación en el DAG. | Test de invalidación de downstream a partir de reporte de QA. | Integrar `ArtifactDag.InvalidateDescendants` dentro de la resolución de retrabajo. |
| **AUDIT-008** | `tests/AMCCA.Core.Tests/` | Suites SPEC/73 y SPEC/74 ausentes. | Suites dedicadas para escenarios C-01..C-14 y X-01..X-16. | Ejecución de escenarios de caos con caídas simuladas y saturación SQLite. | Crear `ConcurrencySpec73Tests.cs` y `ChaosSpec74Tests.cs`. |
| **AUDIT-009** | `src/` / `TOOLS/` | Sin instalador WiX MSI. | Proyecto WiX v4 o v5 que empaquete `AMCCA.exe` en `AMCCA-Setup.exe`. | Test de verificación de existencia y suma SHA-256 de MSI. | Crear proyecto de instalador WiX y scripts de build. |
| **AUDIT-010** | `tests/AMCCA.Core.Tests/AiProviderRealIntegrationTests.cs` | Handler en memoria en lugar de socket HTTP. | Servidor HTTP de prueba sobre loopback (`TestServer` o `HttpListener`). | Test HTTP real contra socket local. | Sustituir `ControlledHttpMessageHandler` por servidor HTTP loopback real. |
| **AUDIT-011** | `.github/workflows/validation.yml` | `runs-on: ubuntu-latest` incompatible con WPF. | CI ejecutándose en `windows-latest` o job separado de Windows para UI. | Ejecución exitosa de CI en Windows. | Añadir job en `windows-latest` para compilar y probar la UI de WPF. |

---

## 16. Release Decision

# **DECISIÓN FINAL: RELEASE = FAIL**

El estado del repositorio presenta garantías de código base de muy alta calidad en contratos de dominio, control de agentes, inmutabilidad de base de datos y aritmética monetaria. Sin embargo, no cumple con los criterios de aceptación normativos para un Release de producción debido a la ausencia de la interfaz de usuario WPF (Fase 16), la ausencia de la capa de experimentos y memoria (Fase 15), la desconexión del handler SSRF en producción, el empaquetado incompleto sin instalador WiX (Fase 18) y la falta de suites formales de caos y concurrencia (Fase 17).
