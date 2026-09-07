# Quinta auditoría — solo código

**Fecha:** 2026-09-06
**Rama:** `fix/audit-remediation`
**Base de comparación:** `origin/main` @ `04ec9f5`
**Alcance:** `src/` completo (~17k líneas), ignorando el corpus `.md`. Cada hallazgo respaldado por
lectura del árbol; cada cierre por una comprobación mecánica reproducible.

Esta auditoría continúa el trabajo de código de `FOURTH_AUDIT_PROJECT_AND_SPEC.md` §0-ter (la sesión
que construyó el pipeline). Volvió a auditar `main` con ojos nuevos tras el merge de los PR #2 y #3.

---

## 1. Los diez hallazgos y su estado

| # | Sev | Hallazgo | Estado | Commit / justificación |
|---|-----|----------|--------|------------------------|
| **H1** | High | **Cero contabilidad de gasto de IA.** `AgentRuntime.RunAgentAsync` descartaba `GatewayTextResponse.InputTokens/OutputTokens`; `RevenueService.RecordCostAsync` no se llamaba desde ningún sitio de `src/`; `contract.MaxCost` era un gate muerto para el gasto de modelo; nunca se escribía una fila `cost_events` para una corrida de agente. El gasto de modelo era invisible para el motor de presupuesto y para el profit. | **CERRADO** | `1a497a3` (captura de tokens) + `02cdc8f` (precio→coste→`cost_events`, D-034). 7 tests. |
| **H2** | High | **Las migraciones copian por posición** (`INSERT INTO x_new SELECT * FROM x`, 10+ sitios; 40 `SELECT *`). Frágil ante una edición retroactiva de una migración temprana. | **Sin cambio — ya guardado.** `TOOLS/validate_package.py::_build_live_schema_via_sqlite` aplica las 10 migraciones contra SQLite real y valida el esquema final contra los contratos (`check_contract_enum_matches_ddl_check`, `check_contract_fields_have_columns`). Un desajuste de columnas por una edición retroactiva rompe el gate (`LiveSchemaBuildError` o deriva de enum/campo). El residual (mismo nº de columnas, dos del mismo tipo intercambiadas) es estrecho y no justifica reescribir DDL congelado. |
| **M1** | Med | **`TimeProvider` al ~5%** (solo `JobManager`/`JobWorkerEngine`; ~55 `DateTimeOffset.UtcNow` restantes en ~18 clases). | **CERRADO (parcial deliberado)** `e02a03c`. `TimeProvider` inyectado en los componentes con lógica de comparación temporal real: `ApprovalManager` (expiración `expires_at > now`), `OAuthManager` (cálculo de `expires_at` del token), `MemoryRetrievalService` (decaimiento de recencia SPEC/22). `TimeProvider.System` registrado como *singleton* de DI. `ApprovalExpiryClockTests` prueba el control por `FakeTimeProvider` sin *sleep* real. Los ~50 sitios restantes son `created_at`/`updated_at`/`occurred_at` de solo escritura, sin comparación y sin ruta de test aseverable; se dejan en el reloj del sistema con el *seam* de inyección ya disponible. |
| **M2** | Med | **`job.attempt` se incrementa al reclamar, no al fallar** (`JobManager.cs:148/211`). Un worker que crashea antes de `FailJob` igual quema intentos. | **Sin cambio — diseño deliberado.** Incrementar al reclamar es la protección contra *jobs* venenosos que matan al worker antes de poder llamar `FailJob` (crash duro, OOM, kill): con incremento solo en `FailJob` nunca llegarían a `DEAD_LETTER`. `RequeueDeadLetterJobForOperatorAsync` (líneas 528-533) ya razona explícitamente sobre no resetear el contador para evitar bucles infinitos. |
| **M3** | Med | **Los errores async de la app WPF desaparecen.** Sin `DispatcherUnhandledException`; `AsyncRelayCommand.Execute` es `async void` sin `catch`; ~12 `_ = XAsync()` en ViewModels. | **CERRADO** | `ce17b5c`. Handlers `DispatcherUnhandledException` / `UnobservedTaskException` / `AppDomain` con logger Serilog a fichero; el único `await` sin guardar de `OnStartup` envuelto. (Los 12 *fire-and-forget* ya tenían `try/catch` interno que enruta a `INotificationService`; se dejaron.) |
| **M4** | Med | **`CONCEPT_SELECTED` (`kind: gate`) cableado a `NoWorkAdvanceHandler`** → auto-avance sin lógica en AUTONOMOUS. Salidas T-003/T-004 (decisión de estrategia + *snapshot* de valor esperado; reserva de presupuesto de scripting) sin implementar → *bypass* silencioso de un gate de decisión. | **CERRADO** | `02cdc8f` (`ConceptSelectionStageHandler`, D-035). 6 tests. |
| **M5** | Med | **Sin test end-to-end del agente real** (`AgentResearchAgent → AgentScriptAgent → QaStageHandler` por `OrchestratorEngine` contra un `IProviderGateway` falso). | **CERRADO** `e02a03c`. `AgentPipelineEndToEndTests`: una producción conducida por `OrchestratorEngine.RunTickAsync` a través de la cadena real RESEARCHING → RESEARCH_VERIFIED → CONCEPT_SELECTED → SCRIPTING → SCRIPT_VERIFIED con los agentes reales `AgentResearchAgent`/`AgentScriptAgent` y el handler real `ConceptSelectionStageHandler`, contra un gateway *scripted*. Asevera transiciones reales, artefacto SCRIPT persistido, concepto `SELECTED` + presupuesto reservado (camino feliz de M4 por el motor) y fila `cost_events` `SETTLEMENT`/`RECONCILED` (H1 por el motor). La mitad generativa de RESEARCHING (bucle de *tools*) sigue cubierta por `AgentResearchAgentContractTests` + `AgentLoopContractTests`. |
| **L1** | Low | **`OAuthManager.RevokeTokenAsync` se tragaba el fallo de revocación** en `catch {}` y luego borraba el secreto local + marcaba DISCONNECTED sin dejar rastro. | **CERRADO** | `23ab13c`. `catch` acotado a `HttpRequestException`/`TaskCanceledException`; cada revocación escribe fila `OAUTH_REVOKED` en `audit_log` (`ALLOWED`/`ERROR`). El *disconnect* local sigue siendo incondicional (hay que poder soltar una cuenta rota). 2 tests. |
| **L2** | Low | **`--orchestrator` hace `new WindowsDpapiSecretStore()`** → `PlatformNotSupportedException` fuera de Windows. | **Sin cambio.** El TFM es `net8.0-windows`; la app no arranca fuera de Windows de todas formas. No-issue funcional. |
| **L3** | Low | **`ModelId "gpt-4o-mini"` *hardcoded*** en `ResearchAgentOptions.Default` y `ScriptAgentOptions.Default`. | **Sin cambio.** Ya está marcado `ponytail:` con su ruta de mejora. El arreglo correcto (resolver desde `model_registry` o de config) es un cambio de comportamiento aparte; D-034 introduce el mecanismo de precios pero no el de selección de modelo. |

**Cerrados con código: H1, M3, M4, L1, M5** (+ base de H1 en `1a497a3`) **y M1** (parcial deliberado: lógica temporal cubierta, timestamps de solo escritura dejados en reloj del sistema).
**Cerrados por análisis (ya cubierto / diseño / TFM): H2, M2, L2, L3.**
**Los diez hallazgos están cerrados.**

---

## 2. Matriz H1–M4 y clasificación P0/P1/P2

| Hallazgo | Clase | Impacto si no se arregla | Entregado |
|---|---|---|---|
| **H1** | **P0** | El sistema autónomo gasta dinero de IA sin medirlo; `MaxCost` y el profit no ven el gasto de modelo; imposible reconciliar contra la factura del proveedor. | Precios de config → `pricing_snapshots` → coste `decimal` por turno → `AccumulatedCost` → *enforcement* de `MaxCost` → una fila `cost_events` `SETTLEMENT` por corrida (`RECONCILED` / `ESTIMATED_UNRECONCILED`). Sin precio configurado: *fail-safe* honesto, nunca cero silencioso ni precio inventado. |
| **M4** | **P1** | Un gate de decisión (`kind: gate`) se salta en silencio en AUTONOMOUS; ninguna decisión de concepto se persiste; el presupuesto de scripting nunca se reserva. | `ConceptSelectionStageHandler`: selecciona (score pre-computado, nunca re-derivado), reserva el presupuesto de scripting, persiste la decisión (link + `SELECTED` + auditoría) o **BLOCK** con `reason_code`. Nunca avanza en silencio. |
| **H2** | **P2** | Fragilidad ante una edición retroactiva improbable de DDL congelado. | Ya guardado por `_build_live_schema_via_sqlite`. Sin acción. |
| **M1** | **P2** | Deuda de testabilidad (reloj no inyectable). | `TimeProvider` inyectado en la lógica de comparación temporal (`ApprovalManager`/`OAuthManager`/`MemoryRetrievalService`) + *singleton* de DI + test determinista. Timestamps de solo escritura: fuera de alcance (sin ruta de test). |
| **M2** | **P2** | (No es defecto — diseño de protección anti-*poison-job*.) | Sin acción. |
| **M3** | **P1** | Una excepción async inesperada en el hilo UI mata el proceso sin log. | Red global de excepciones + logger a fichero. |
| **M5** | **P2** | Menor confianza en la ruta agente-real E2E. | `AgentPipelineEndToEndTests`: cadena RESEARCHING→…→SCRIPT_VERIFIED por el motor con agentes y handlers reales. |
| **L1** | **P1** | Un fallo de revocación de token OAuth se perdía sin rastro. | Auditado (`OAUTH_REVOKED`, `ERROR`). |
| **L2 / L3** | **P2** | Marginal / ya mitigado. | Sin acción. |

---

## 3. Revisión contra `CLAUDE.md` ("Never do")

| Regla | Veredicto |
|---|---|
| No inventar APIs / capacidades de proveedor | **OK.** Los precios de modelo vienen de `config.providers.gateway.model_pricing` (el operador), no de una lista *hardcoded*. `PricingSnapshotModelPricing` los lee y materializa. |
| No marcar éxito externo sin evidencia autoritativa | **OK.** `cost_events.reconciliation_state = RECONCILED` solo si se resolvió un `pricing_snapshot` real **y** el gateway devolvió *usage*; si no, `ESTIMATED_UNRECONCILED`. |
| No dejar que un agente escriba filas arbitrarias | **OK.** El modelo no escribe filas. `AgentRuntime` (infra) escribe **una** fila `cost_events` por columnas fijas vía puerto tipado. `ConceptSelectionStageHandler` es infra de orquestación, no el agente. |
| No guardar secretos en fuente / YAML / logs | **OK.** `config.example.yaml` lleva `source_ref: "REPLACE-WITH-..."`. Los precios no son secreto. |
| No saltarse gates de QA / presupuesto / política para pasar una demo | **OK — lo contrario.** M4 endurece el gate (BLOCK en vez de avance silencioso); H1 hace que `MaxCost` de verdad *enforce*. |
| No sustituir una integración que falla por un adaptador de éxito falso | **OK.** `NullModelPricing`/`NullModelCostStore` son *no-ops* explícitos usados solo donde no hay nada cableado (tests/tools); no fingen éxito. `PricingSnapshotModelPricing` devolviendo `null` → `ESTIMATED_UNRECONCILED`, no éxito falso. |
| No usar coma flotante para dinero | **OK.** `ModelCostCalculator` es todo `decimal`. `opportunities.score` es `REAL` pero es un score pre-computado (no dinero); se lee como TEXT (`CAST`), se ordena en SQL, nunca se hace aritmética float sobre él. `expected_cost` → `Money.TryParse` → `decimal`. |
| No editar a mano un artefacto generado (`SCHEMAS/*.json`, `MANIFEST.md`) | **OK.** `config.schema.json` regenerado vía `generate_artifacts.py --regen` tras editar el generador. `MANIFEST` regenerado vía `validate_package.py --regen`. |
| No añadir framework / servicio externo sin ADR | **OK.** Ninguno nuevo. Se añadieron D-034 y D-035 igualmente. |
| No resolver una contradicción entre dos documentos eligiendo uno | **OK.** M4: la resolución (select+BLOCK, a favor de SPEC/13) fue una **decisión explícita del propietario**; D-035 la documenta. |

Loop de implementación de `CLAUDE.md`: contrato identificado (SPEC/20, SPEC/21, SPEC/13,
`cost-event.schema.json`) → dominio determinista primero (`ModelCostCalculator` puro) → borde de
adaptador tras puerto (`IModelPricing`, `IModelCostStore`, `IStageHandler`) → validación de schema en
el borde (`config.schema.json` + `Money.TryParse`) → tests unitarios + integración (13 nuevos) →
tests de fallo (precio ausente, reserva rechazada, `MaxCost` excedido, oportunidad rechazada, sin
oportunidad) → gate + suite.

**Nota de contrato:** `config.schema.json` gana la propiedad opcional `model_pricing` bajo
`providers.gateway`; es aditiva y retro-compatible con todo config `3.1.0` existente, por lo que el
`schema_version` const permanece en `3.1.0` (mismo criterio que D-031..D-033, añadidos dentro de
3.1.0).

---

## 4. Evidencia — SHA probado

- **SHA de la rama:** `02cdc8ff2baf0148381efee05560daf4c9b824fb` (`fix/audit-remediation`).
- **Suite local:** `765 passed, 0 failed, 0 skipped` (Debug y Release; `AMCCA.Core.Tests`).
- **`validate_package.py`:** 68/68.
- **`test_mutations.py`:** 19/19 · **`test_certification_mutations.py`:** 15/15.
- **`release_gate.py`:** PASS (checks de etapa de especificación; 17/19/20 requieren implementación en
  ejecución y 27 requiere `pip-tools`, ambos N/A en este entorno — sin cambio respecto a `main`).
- **Build Release:** `AMCCA.sln` 0 avisos / 0 errores (`TreatWarningsAsErrors`).
- **CI (GitHub Actions, run `34039408310`, commit `02cdc8f`):** `validate-spec` ✓ **success**;
  `Windows Desktop & WPF Solution Validation` ✓ **success**. Ambos jobs verdes.

---

## 5. Estado de certificación (paso 9 — no declarar CERTIFIED sobre un SHA histórico)

La certificación previa (`AUDIT/FINAL_RELEASE_CERTIFICATION.md`, source SHA `9ba76f4`) quedó
**invalidada** por los commits `23ab13c → bef7cba` de esta remediación, bajo su propia regla de
integridad #4.

**Re-certificación completada.** Tras el merge a `main` (PR #4 → commit `08cc158`), el
*release certification pipeline* de CI se ejecutó sobre ese commit exacto:

- **CI run `34040715143`**, ambos jobs `success`, con `CI commit SHA == source SHA`.
- Paso "Run Deterministic Release Certification Pipeline" (`release_certification.ps1`):
  **`CERTIFICATION COMPLETE: RELEASE PASS`**, 15/15 invariantes de release estrictos.
- Release `.trx`: `768 total | 768 passed | 0 failed | 0 skipped`. Build: `0 errors | 0 warnings`.
- Instalador: `AMCCA-Setup.exe` 62,376,023 B · `AMCCA-Setup.msi` 61,555,672 B ·
  `AMCCA-Desktop-win-x64.zip` 72,865,105 B; PE32+ válido; `SHA256SUMS.txt` consistente.

`AUDIT/FINAL_RELEASE_CERTIFICATION.md` reescrito para certificar el **source SHA
`08cc158c902c1bfc4ccc17dc610a353cde4a8511`**, sustituyendo al `9ba76f4`. El commit documental que
añade ese documento no es el SHA certificado (regla #2).
