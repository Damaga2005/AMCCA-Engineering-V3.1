# AMCCA Engineering V3.1 — Second Forensic Audit: Stub & Semantic Bypass Report

> **Modo:** RED TEAM / RELEASE BLOCKER / ZERO TRUST  
> **Fecha:** 2026-09-03  
> **Commit Auditado:** `d1d9fefe32be33fc0b34130598cb535a3bc6e398`  

---

## 1. Resumen Ejecutivo de Stubs y Simulaciones

La búsqueda estática y semántica sobre el árbol de producción (`src/`) y pruebas (`tests/`) arrojó los siguientes hallazgos:

| Categoría | Tokens Buscados | Coincidencias en `src/` | Clasificación de Riesgo | Impacto en Release |
|---|---|---|---|---|
| **Marcadores Crudos** | `TODO`, `FIXME`, `NotImplementedException`, `NotSupportedException`, `throw new Exception` | 0 | LIMPIO | N/A |
| **Palabras Clave de Simulación** | `stub`, `placeholder`, `dummy`, `fake`, `mock`, `coming soon` | 0 | LIMPIO | N/A |
| **Stubs Semánticos / Implementaciones Vacías** | No-op methods, handlers desconectados, bypasses de red | 4 | **CRÍTICO / HIGH** | **BLOQUEA RELEASE** |

---

## 2. Detección Detallada de Stubs Semánticos y Bypasses de Producción

### STUB-001 — Consola Vacía en lugar de Desktop UI (Fase 16)
- **Ubicación:** `src/AMCCA.App/Program.cs` (Líneas 1-22)
- **Código detectado:**
  ```csharp
  public static async Task<int> Main(string[] args)
  {
      Console.WriteLine("AMCCA Engineering V3.1 Runtime");
      Console.WriteLine("System initialized successfully.");
      ...
      await Task.CompletedTask;
      return 0;
  }
  ```
- **Naturaleza del defecto:** Stub estructural. El archivo simula un punto de entrada de aplicación retornando código 0 y escribiendo cadenas por consola, sin inicializar un Host WPF, ventana principal, contexto de navegación ni inspectores exigidos por `SPEC/65` y `SPEC/66`.
- **Severidad:** **CRITICAL** (Release Blocker).

---

### STUB-002 — Desconexión del Handler de Sockets SSRF en Producción (Fase 8)
- **Ubicación:** `src/AMCCA.Core/Security/SsrfValidator.cs` (Líneas 152-224)
- **Código detectado:**
  ```csharp
  public static SocketsHttpHandler CreateSafeSocketsHttpHandler() { ... }
  ```
- **Naturaleza del defecto:** Implementación desconectada / Dead Code. El método que garantiza la mitigación de DNS Rebinding y TOCTOU a nivel de socket de red existe y pasa tests unitarios en `tests/`, pero **ningún componente de producción en `src/` (incluyendo `ResearchService` o adaptadores de red) lo invoca o lo inyecta en su `HttpClient`**. El fetch de producción opera con `HttpClient` estándar desprotegido frente a DNS rebinding dinámico.
- **Severidad:** **CRITICAL** (Security Bypass / Dead Code).

---

### STUB-003 — Adaptador de Plataforma Inexistente / Operación Simulada (Fase 12)
- **Ubicación:** `src/AMCCA.Core/Publishing/PlatformHub.cs` y `IPlatformAdapter.cs`
- **Código detectado:**
  `IPlatformAdapter` únicamente define `PollAuthoritativeEvidenceAsync`. No existe ningún método `PublishAsync`, ni adaptadores de red para YouTube, TikTok, o Instagram.
- **Naturaleza del defecto:** Stub funcional. La publicación se reduce a una inserción local en SQLite de la tabla `publications` con estado `QUEUED`, sin ninguna capacidad de despacho hacia APIs remotas ni resolución OAuth.
- **Severidad:** **HIGH** (Garantía no implementada).

---

### STUB-004 — Rework Resolver Reducido a Contador Aislado (Fase 11)
- **Ubicación:** `src/AMCCA.Core/QA/ArtifactDag.cs` (Líneas 73-87)
- **Código detectado:**
  ```csharp
  public class DagReworkResolver
  {
      private readonly int _maxReworkAttempts;
      public DagReworkResolver(int maxReworkAttempts = 3) { _maxReworkAttempts = maxReworkAttempts; }
      public bool CanAttemptRework(int currentAttempts) => currentAttempts < _maxReworkAttempts;
  }
  ```
- **Naturaleza del defecto:** Stub algorítmico. `SPEC/37` exige que el resolvedor de retrabajo tome un fallo de QA, identifique el artefacto culpable, trace sus dependientes en el DAG e invalide exclusivamente el subgrafo afectado. La clase en producción únicamente compara dos enteros (`currentAttempts < _maxReworkAttempts`), delegando la invalidación del DAG a tests que llaman manualmente a métodos independientes.
- **Severidad:** **HIGH** (Lógica incompleta).

---

### STUB-005 — Ausencia Absoluta de Servicios para Fase 15 (Memory / Genome)
- **Ubicación:** Árbol completo `src/AMCCA.Core/`
- **Naturaleza del defecto:** Omisión total. No existen archivos de código ni stubs: la funcionalidad no fue implementada en C#, y solo existe una tabla vacía en la base de datos.
- **Severidad:** **CRITICAL** (Fase de BUILD_ORDER omitida).
