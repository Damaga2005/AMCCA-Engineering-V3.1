# Cuarta auditoría — Proyecto y Especificación

**Fecha:** 2026-09-04
**Rama auditada:** `claude/amcca-spec-audit-repo-hx631m`
**Base de comparación:** `origin/main`
**Alcance:** el corpus normativo completo (83 SPEC, 12 BLUEPRINT, 17 SCHEMAS, DECISIONS) y su
correspondencia con la implementación.

Esta auditoría se diferencia de `AMCCA_SPEC_01_83_AUDIT.md` en dos cosas: audita **la especificación
además del código**, y toda afirmación está respaldada por una comprobación mecánica reproducible sobre
el árbol, no por lectura impresionista.

---

## 0. Qué no se ha podido verificar

**No se ha compilado ni ejecutado la suite .NET.** No hay SDK de .NET en el entorno de auditoría. Todo
lo relativo a compilación y resultado de tests es, por tanto, **no verificado**. Las verificaciones que
sí se han ejecutado son las herramientas Python del repositorio (`validate_package.py`,
`release_gate.py`, `test_repository_hygiene.py`, `test_certification_mutations.py`) y análisis estático
propio sobre los contratos, el DDL y el código fuente.

Dos comprobaciones del gate fallan por entorno, no por el proyecto: `jsonschema` no está instalado.

---

## 1. Hallazgo principal: la trazabilidad del informe anterior está rota

`AMCCA_SPEC_01_83_AUDIT.md` presenta una tabla «Estado SPEC 01 → 83» con 83 entradas numeradas. Se ha
comparado cada número con el fichero `SPEC/NN_*.md` real:

| | |
|---|---:|
| Entradas de la tabla | 83 |
| Coinciden con el fichero SPEC real | **15** |
| **No** coinciden | **68** |

Ejemplos:

| Nº | Dice el informe | Fichero SPEC real |
|---:|---|---|
| 03 | Database | `03_CONFIGURATION.md` |
| 12 | Domain Model | `12_STATE_MACHINE.md` |
| 15 | Jobs | `15_IDEMPOTENCY.md` (Jobs es el 14) |
| 35 | Hooks | `35_QA_ENGINE.md` (Hooks es el 31) |
| 61 | Inspector | `61_UI_FLOWS.md` (el Inspector se especifica en el 60) |
| 62 | Job Queue | `62_UI_STATE.md` (Job Queue se especifica en el 60 y el 14) |
| 73 | Chaos | `73_CONCURRENCY_TESTS.md` (Chaos es el 74) |
| 83 | Final Acceptance | `83_ANTIGRAVITY_EXECUTION.md` |

El desplazamiento no es un desfase constante: el informe construyó **una taxonomía propia de áreas**,
numerada 1–83 por coincidencia con el tamaño del corpus. Sus juicios por área siguen siendo en gran
medida útiles —está claro que miró el código real— pero **ninguna de sus citas «SPEC NN» es una
referencia válida** al documento normativo.

**Consecuencia práctica y comprobada:** el trabajo de remediación P0 realizado sobre esa base heredó las
citas erróneas. Cuatro citas `SPEC/61` (Inspector), una `SPEC/30` (claims), una `SPEC/22` (presupuestos)
y una `SPEC/59` (control de operador) apuntaban al documento equivocado. Corregidas en esta rama.

**Recomendación:** anotar el informe original indicando que su columna numérica es un índice interno, o
reemplazar sus números por los nombres de fichero SPEC reales. Mientras no se haga, cualquier plan
derivado de él arrastrará trazabilidad falsa.

**Resuelto:** se añadió una nota al inicio de `AMCCA_SPEC_01_83_AUDIT.md` y otra justo antes de la tabla
de la sección 3, dejando explícito que la columna «SPEC» es un índice interno 1–83 y no una referencia
válida a `SPEC/NN_*.md`. No se renumeró la tabla ni se tocó ningún otro contenido del informe, para
preservarlo tal como se produjo.

---

## 2. Hallazgos sobre la especificación

### 2.1 Contratos declarados pero no aplicados (16 columnas) — **Resuelto (migración 010)**

`validate_package.py` comprueba que toda tabla tenga contrato (`db.every_table_has_contract`, PASS),
pero **no comparaba los `enum` del contrato con las restricciones `CHECK` del DDL**. Comparándolos:

| Columna | Contrato | DDL (estado original) |
|---|---:|---|
| ~~`productions.state`~~ | 32 valores | ~~sin `CHECK`~~ **Resuelto** |
| ~~`jobs.state`~~ | 9 valores | ~~sin `CHECK`~~ **Resuelto** |
| ~~`tool_runs.state`~~ | 6 valores | ~~sin `CHECK`~~ **Resuelto** |
| ~~`tool_runs.side_effect_class`~~ | 5 valores | ~~sin `CHECK`~~ **Resuelto** |
| ~~`agent_runs.state`~~ | 7 valores | ~~sin `CHECK`~~ **Resuelto** |
| ~~`events.aggregate_type`~~ | 9 valores | ~~sin `CHECK`~~ **Resuelto** |
| ~~`audit_log.outcome`~~ | 6 valores | ~~sin `CHECK`~~ **Resuelto** |
| ~~`productions.autonomy_mode`~~ | 3 valores | ~~sin `CHECK`~~ **Resuelto** |
| ~~`qa_reports.stage`, `claims.materiality`, `claims.subject_class`, `rights_records.provenance`, `rights_records.commercial_use`, `rights_records.modification`, `referral_links.validation_method`, `analytics_snapshots.provenance`~~ | — | ~~sin `CHECK`~~ **Resueltos** |

Esto contradecía el principio que el propio proyecto declara en D-026: *«This gate is **structural**, not
merely procedural… This holds even if the preflight code path that is supposed to enforce it has a
bug»*. El proyecto creía en la aplicación estructural pero solo la aplicaba en algunas columnas.

Dos casos merecían atención especial, ambos cerrados:

- **`productions.state` sin `CHECK`** era la máquina de estados central del sistema sin ninguna defensa
  a nivel de base de datos. Ahora tiene un `CHECK` con los 32 valores exactos del contrato.
- **`tool_runs.side_effect_class` sin `CHECK`** era relevante para seguridad: existía la restricción
  condicional `CHECK(side_effect_class <> 'EXTERNAL_UNSAFE' OR intent_id IS NOT NULL)`, pero como el
  dominio de valores no estaba acotado, un valor mal escrito (`'external_unsafe'`, espacio final)
  **evadía la exigencia de intent** en lugar de ser rechazado. La defensa estructural fallaba en abierto;
  ahora el `CHECK` de dominio (`side_effect_class IN ('PURE','READ','LOCAL_WRITE','EXTERNAL_IDEMPOTENT','EXTERNAL_UNSAFE')`)
  cierra ese hueco: un valor fuera de esos cinco es rechazado antes de que la condicional de intent
  entre siquiera en juego.

**Cómo se cerraron las 17 columnas (16 tablas del hallazgo original, más `cost_events.kind`/
`publications.state` de §2.2 en la misma migración) — migración 010:** SQLite no tiene
`ALTER TABLE ... ADD CONSTRAINT`, así que añadir un `CHECK` a una columna existente exige recrear la
tabla completa. Cinco de las trece tablas tocadas —`agent_runs`, `jobs`, `productions`, `publications`,
`qa_reports`— son objetivo de FK desde otras tablas vivas (`productions` en particular, desde `claims`,
`rights_records`, `qa_reports`, `artifacts`, `artifact_manifests`, `production_versions` y `hooks`).

El primer intento (`DROP TABLE`+`CREATE TABLE` bajo el mismo nombre, con
`PRAGMA defer_foreign_keys = ON`) se verificó contra SQLite real (3.45.1) que **falla en el `COMMIT`**
con `"FOREIGN KEY constraint failed"` pese a que `PRAGMA foreign_key_check` justo antes no reporta
ninguna violación — un contador interno de FK diferida que el `DROP` incrementa y que una tabla
homónima recreada y repoblada después, en la misma transacción, no decrementa. Renombrar la tabla en
vez de borrarla tampoco lo evita: el renombrado reescribe automáticamente la cláusula `FOREIGN KEY` de
*cualquier otra* tabla que la referenciara, y arreglar eso (recreando cada una de esas tablas para que
vuelvan a apuntar al nombre real) resultó ser transitivo — arreglar una tabla hija "envenena" a sus
propias tablas hijas, verificado con una réplica mínima de tres niveles abuelo/padre/hijo.

El patrón que sí funciona, y que es el recomendado por la propia documentación de SQLite para este caso
exacto ("Making Other Kinds Of Table Schema Changes"): desactivar la aplicación de FK durante el
tiempo que dura la reconstrucción, hacer el `DROP`+`CREATE` normal bajo el mismo nombre para cada
tabla que lo necesite, reactivar la aplicación de FK y verificar con un `PRAGMA foreign_key_check` real
antes de confiar en el resultado. El problema es que `PRAGMA foreign_keys` es un no-op documentado una
vez que ya hay una transacción abierta, y `MigrationService.UpgradeAsync` envuelve el `UpSql` de cada
migración en una única `connection.BeginTransaction()`. La migración 010 se ejecuta por tanto como una
excepción con nombre (`MigrationsRequiringForeignKeysOff` en `MigrationService.cs`): su SQL gestiona su
propia atomicidad (`PRAGMA foreign_keys = OFF; BEGIN; ...; COMMIT; PRAGMA foreign_keys = ON;`) sin
transacción ADO ambiente, y `UpgradeAsync`/`RollbackAsync` verifican `PRAGMA foreign_key_check` por su
cuenta después, lanzando `AmccaException` si queda alguna violación, en vez de fiarse de que el propio
`COMMIT` haya funcionado (D-026 aplicado a la propia migración, no solo a la columna).

**Divergencias de código descubiertas al trazar cada escritor real (no solo el DDL) mientras se cerraba
esto** — el check automatizado solo compara contrato↔DDL, así que estos cuatro no aparecían en ningún
informe hasta que se revisó manualmente cada sitio que escribe uno de los 17 campos:

- `PlatformHub.CreatePublicationAsync` escribía `state = 'QUEUED'` — no está en el dominio de
  `publication.schema.json` (que empieza en `INTENT_CREATED`). Corregido, junto con el valor por defecto
  del propio modelo `PublicationRecord.State` (mismo valor, mismo bug si algún día se construye el
  objeto sin fijar `State` explícitamente).
- `OperatorControlService` escribía `Outcome: "COMMITTED"` en tres sitios (kill switch, decisión de
  aprobación, requeue de dead-letter) — no está en el dominio de `audit.schema.json`. Corregido a
  `APPROVED`.
- `PromptService.RunAgentAsync` escribía `State = "RUNNING"` para un `agent_runs` recién creado — no
  está en el dominio de `agent-run.schema.json` (que usa `STARTED`). Corregido, junto con el valor por
  defecto de `AgentRunRecord.State` (mismo modelo de bug latente que `PublicationRecord`).
- `ProductionsViewModel.CreateProductionAsync` (la única pantalla WPF que crea producciones) llamaba a
  `CreateProductionAsync(..., autonomyMode: "COLLABORATIVE", ...)` — `COLLABORATIVE` nunca estuvo en el
  dominio de `production.schema.json` (`MANUAL`/`ASSISTED`/`AUTONOMOUS`). Sin el `CHECK` nuevo esto
  nunca se habría detectado: cualquier producción creada desde la UI real habría escrito un valor que el
  contrato prohíbe. Corregido a `ASSISTED`.

Por el mismo motivo se auditaron y corrigieron **19 sitios en 8 ficheros de test** que sembraban
`productions.state`/`autonomy_mode` con valores heredados que el DDL nunca comprobó
(`FULL_AUTONOMY`→`AUTONOMOUS`, `COLLABORATIVE`→`ASSISTED`, y estados como `RENDERING`, `RENDER_DONE`,
`DRAFT`, `PUBLISHED`, `RESEARCH`, `SCRIPT_GEN`, `APPROVAL_PENDING` mapeados a su valor real más cercano
en la máquina de 32 estados), más 2 sitios adicionales en un test de `events`/`audit_log`
(`aggregate_type='PRODUCTION'` en mayúsculas cuando el dominio es minúsculas; `outcome='SUCCESS'`, que
nunca estuvo en el dominio). Ninguno de estos tests aserciona sobre el valor literal en sí — son datos de
relleno para satisfacer una FK `NOT NULL` — así que el `CHECK` nuevo no cambia lo que cada test verifica,
solo exige que el relleno sea un valor real del contrato.

### 2.2 Contradicciones directas contrato ↔ DDL (3)

| Columna | Contrato permite y el DDL rechaza | DDL permite y el contrato rechaza |
|---|---|---|
| ~~`audit_log.actor_type`~~ | ~~`ORCHESTRATOR`, `RECONCILER`, `SCHEDULER`~~ | — **Resuelto (migración 004)** |
| ~~`cost_events.kind`~~ | ~~`ESTIMATE`, `RELEASE`~~ | ~~`REFUND`~~ **Resuelto (migración 010)** |
| ~~`publications.state`~~ | — | ~~`QUEUED`, `RECONCILING`, `RETRACTED`, `SUBMITTED`~~ **Resuelto (migración 010)** |

**Corregido esta sesión (revisión de la propia auditoría, no trabajo nuevo):** esta entrada estaba
desactualizada. `audit_log.actor_type` ya lleva `CHECK(actor_type IN ('OPERATOR','SCHEDULER','ORCHESTRATOR','RECONCILER','SYSTEM'))`
desde la migración 4 (verificado leyendo el DDL real vía `_extract_migrations_from_csharp` +
SQLite, no solo la migración 1 que sí tenía el `CHECK` de dos valores que describía esta sección).
Ningún escritor actual usa `SCHEDULER`/`ORCHESTRATOR`/`RECONCILER` todavía (`grep` de `ActorType\s*[:=]`
en `src/AMCCA.Core` solo encuentra `OPERATOR` y `SYSTEM`), pero la columna ya está lista para cuando
exista ese escritor — el mismo tratamiento honesto que `analytics_snapshots.source_account_id` en la
migración 007: hueco de contrato cerrado en el esquema, sin fingir un caso de uso que aún no existe.

Además, `cost_events.reconciliation_state` está declarado en el contrato y **la columna no existe** en
el DDL: un campo de contrato sin almacenamiento.

**Ampliado tras construir el check automatizado (§4, punto ciego 4):** no era un caso aislado. Comparando
cada propiedad plana de cada contrato mapeado contra la columna real (excluyendo las 5 correspondencias
verificadas como diseño correcto — el propio PK bajo otro nombre en `cost-event`/`claim`/`rights`/
`referral`/`analytics.schema.json` — y las 3 de `lease_owner`/`lease_until`/`heartbeat_at` de
`job.schema.json`, normalizados en `leases` y unidos por `job_id`), aparecían 20 campos de contrato sin
ninguna columna real:

| Tabla | Campos sin columna (estado original) |
|---|---|
| `cost_events` | ~~`schema_version`~~ (mig. 008), ~~`agent_run_id`~~, ~~`model_id`~~, ~~`provider_request_id`~~, ~~`units`~~ **falso positivo**, ~~`pricing_snapshot_id`~~ (nulable, ver más abajo), ~~`reconciliation_state`~~, ~~`budget_id`~~ — **todo resuelto** (mig. 009) |
| `jobs` | ~~`schema_version`~~ (mig. 008), ~~`scheduled_at`~~, ~~`deadline_at`~~, ~~`estimated_cost`~~, ~~`reserved_cost`~~, ~~`currency`~~, ~~`causation_id`~~, ~~`last_error_code`~~ — **todo resuelto** (mig. 009) |
| ~~`referral_links`~~ | ~~`brand`, `commission_model`, `disclosure_required`~~ **No eran un hueco** |
| ~~`analytics_snapshots`~~ | ~~`source_account_id`~~ **Resuelto** (migración 007) |

**`referral_links.brand`/`commission_model`/`disclosure_required` investigados y descartados como
defecto:** al mirar `referral_programs` (la tabla que `referral_links.program_id` referencia) resultó que
esos tres campos **ya existen ahí** — `brand`, `commission_model` y `disclosure_required` son propiedades
del *programa* de afiliación, no de cada *enlace* individual, y `referral.schema.json` modela una vista
aplanada de enlace+programa igual que `job.schema.json` modela job+lease. Añadirlos como columnas nuevas en
`referral_links` habría **duplicado un campo de cumplimiento crítico en dos tablas que podrían desincronizarse**
— exactamente el error que se evitó al no repetir `lease_owner` en `jobs`. Se registraron en
`_FIELD_HAS_NO_OWN_COLUMN_BY_DESIGN` como normalizados, no como pendientes.

**`analytics_snapshots.source_account_id` sí era un hueco real, y se cerró:** migración 007 añade la
columna, nullable y con FK contra `platform_accounts` (coincide con la propia descripción del contrato,
"ULID, generado localmente... un identificador externo nunca es clave primaria", D-003). Ninguna tabla
tiene código que escriba en ella todavía (ni `referral_links`/`referral_programs` ni `analytics_snapshots`
tienen un solo `INSERT` en `src/AMCCA.Core`), así que fue un cambio puramente aditivo verificado contra
SQLite real (UP, DOWN, y el rechazo de la FK con un id inexistente), sin ninguna fila existente que
reconciliar. Se regeneró `SPEC/11_DATABASE_SCHEMA.md` con las funciones reales del generador (no a mano) y
se comprobó que el diff resultante es exactamente esa fila, sin arrastrar los ~20 ficheros de conversión
CRLF→LF que una regeneración completa produce en este entorno.

**`cost_events.schema_version` y `jobs.schema_version` también se cerraron:** D-004 exige que "todo objeto
de contrato persistido lleva `schema_version`", `job.schema.json`/`cost-event.schema.json` lo declaran
requerido, e incluso el propio modelo de `generate_artifacts.py` para `SPEC/11` ya listaba la columna en
ambas tablas — SPEC/11 llevaba tiempo siendo *aspiracional*, no descriptivo, en este único punto, porque
nada comparaba ese modelo contra el DDL real de `MigrationService.cs`. Migración 008 añade la columna con
`DEFAULT '3.1.0'` (no un `NOT NULL` a secas, porque a diferencia de `referral_links`/`analytics_snapshots`
estas dos tablas sí tienen escritores reales y podrían tener filas existentes) y se verificó contra SQLite
real que una fila insertada *antes* de la migración recibe el valor por defecto en el backfill.
`JobManager.EnqueueJobAsync`/`GetJobAsync` y `RevenueService.RecordCostAsync` se actualizaron para
fijar/leer el campo explícitamente en vez de depender solo del default.

**Falso positivo encontrado y corregido en el propio check:** `cost_events.units` aparecía en la lista de
huecos, pero `units` es un objeto anidado (`{"oneOf": [{"type": "object"}, {"type": "null"}]}`) — no tiene
columna plana por diseño, igual que cualquier otro campo JSON de este proyecto (`payload_json`,
`restrictions_json`...). `contracts.fields_have_columns` solo miraba `type`/`properties`/`items` en el
nivel superior del `spec`, así que un objeto envuelto en `oneOf` (el patrón que este proyecto usa en todas
partes para "opcional y nulable") se colaba como si fuera un campo plano. Corregido con `_is_nested_shape()`,
que también mira dentro de `oneOf`/`anyOf`. No se añadió ninguna columna `units_json`: el contrato nunca
tuvo un campo llamado `units` esperando una columna homónima, así que no había nada que corregir en el
esquema, solo en el check que lo mal interpretaba.

**Los 13 restantes, cerrados (migración 009):** `cost_events` ganó `agent_run_id` (FK a
`agent_runs.run_id` — **no** `id`, verificado contra el DDL real en vez de asumido, ya que es la única
tabla del paquete cuya PK no se llama `id`), `model_id`, `provider_request_id`, `budget_id` (FK a
`budgets.id`), `pricing_snapshot_id` y `reconciliation_state`. `jobs` ganó `causation_id`, `currency`,
`deadline_at`, `estimated_cost`/`reserved_cost` (con el mismo `CHECK` de no-negatividad que usa el resto
del proyecto para dinero), `last_error_code` y `scheduled_at`.

`reconciliation_state` es un campo **requerido** por el contrato, y sí se pudo cerrar sin trampas: un coste
recién grabado siempre significa "aún no reconciliado contra el proveedor" — `DEFAULT 'ESTIMATED'` no es
una suposición, es lo que `RecordCostAsync` ya significaba implícitamente. `RecordCostAsync` ahora lo fija
explícitamente, verificado contra SQLite real que una fila anterior a la migración recibe el backfill
correcto y que el `CHECK` rechaza un valor inválido.

`pricing_snapshot_id` **también es requerido por el contrato, y aquí sí hubo que apartarse deliberadamente**:
ninguna tabla `pricing_snapshots` tiene código que la escriba (esa tubería de ingesta de precios no existe
todavía), así que un `NOT NULL` + FK real habría hecho fallar **toda** grabación de coste, rompiendo la
funcionalidad para "corregir" un hueco documental. Se añadió como `NULL`able con FK activa — una divergencia
contrato↔DDL real y deliberada (requerido vs nulable) que ningún check actual detecta, dejada aquí explícita
en vez de disimulada. Es la dirección seguro-por-defecto: un valor que nada puede rellenar correctamente hoy
no debe fingirse obligatorio con un centinela inventado.

Los otros 10 campos (`model_id`, `provider_request_id`, `agent_run_id`, `budget_id` en `cost_events`;
`causation_id`, `currency`, `deadline_at`, `estimated_cost`, `reserved_cost`, `last_error_code`,
`scheduled_at` en `jobs`) se añaden nulables y sin escritor que los rellene todavía — mismo tratamiento
honesto que `analytics_snapshots.source_account_id` en la migración 007: la columna existe y está lista
para quien la necesite, pero ningún código la usa aún porque ninguno tiene ese dato disponible hoy
(agendar un job con fecha límite, reservar presupuesto por adelantado, o etiquetar el código de error
estructurado de un fallo son funcionalidades que no existen todavía, no columnas mal puestas).

`contracts.fields_have_columns` pasa en verde por primera vez desde que se creó.

### 2.3 Contradicción contrato ↔ implementación — **Resuelto (migración 006)**

`job.schema.json` enumera `SUCCEEDED`; `JobManager.CompleteJobAsync` escribía `COMPLETED`. Como
`jobs.state` no tenía `CHECK` (§2.1, resuelto en la migración 010), nada lo detectaba.

**Corregido esta sesión (revisión de la propia auditoría, no trabajo nuevo):** esta sección estaba
desactualizada — `JobManager.CompleteJobAsync` ya escribe `'SUCCEEDED'` (verificado con `grep`, dos
sitios), desde la migración 6, que además renombra a `SUCCEEDED` cualquier fila `COMPLETED` grabada por
una versión anterior del código antes de que la migración 010 pudiera añadir el `CHECK` que ahora lo
haría imposible reintroducir.

### 2.4 Códigos de error

| | |
|---|---:|
| Catalogados en `SPEC/05` | 40 |
| Constantes en `AmccaErrors.cs` | 34 |
| **Lanzados por el código pero NO catalogados en ningún SPEC** | **6** |
| Catalogados pero nunca lanzados por ningún código | 7 |

No catalogados: `AMCCA-POL-001`, `AMCCA-POL-003`, `AMCCA-POL-004`, `AMCCA-QA-003`, `AMCCA-RES-003`,
`AMCCA-STM-003`. Esto choca frontalmente con SPEC/60 obligación 6 y SPEC/62 («*Every error reaching the
UI carries a code from `SPEC/05`*»): `AMCCA-POL-004` es precisamente el que ve el operador cuando se le
deniega una acción protegida, y no está en el catálogo.

`refs.all_error_codes_catalogued` pasaba en verde porque **solo escanea ficheros `.md`**: exige que todo
código citado en prosa esté catalogado, y nunca mira el `.cs` que lo lanza. Redactar esta auditoría lo
puso en rojo de inmediato, lo que confirma el punto ciego de forma accidental pero contundente.

**Remediado en esta rama:** los seis códigos se han añadido a la tabla de `SPEC/05`, con la categoría
que usa realmente el código en el punto donde se lanza, no una inventada. Dos matices para revisión del
responsable de la especificación:

- ~~`AMCCA-POL-004` se cataloga como `SECURITY`~~ **Resuelto**: recategorizado a `USER_ACTION_REQUIRED`
  tanto en `SPEC/05` como en los 5 puntos donde `ApprovalManager` lo lanza (`ErrorCategory` es solo
  metadata del error — no hay ninguna rama de código en el repositorio que decida comportamiento por su
  valor, y ningún test afirmaba sobre la categoría, solo sobre el código `AMCCA-POL-004` — así que el
  cambio no tiene riesgo de comportamiento). Coincide con la convención ya usada por `AMCCA-JOB-003`,
  el mismo patrón "espera a un operador".
- `AMCCA-QA-003` y `AMCCA-RES-003` están declarados pero **ningún código los lanza**. Se catalogan
  según su propio comentario; procede decidir si son implementación pendiente o constantes muertas.
  **Resuelto**: `AMCCA-RES-003` es un duplicado exacto de `AMCCA-SEC-003` — `SsrfValidator` ya lanza
  `AMCCA-SEC-003` para todo rechazo de dominio/SSRF, así que `RES-003` nunca tenía un camino que lo
  disparara; no era implementación pendiente, era una constante redundante. `AMCCA-QA-003` sí es
  implementación pendiente genuina: `QaVerdictEvaluator` no tiene ningún concepto de "perfil de umbral"
  con nombre, solo recibe `minOverall`/`minCritical` fijos del llamador, así que la condición que
  describe ("perfil desconocido o inválido") no puede ocurrir hasta que se construya esa función. En
  ambos casos se mantienen catalogados en `SPEC/05` con una nota explícita en vez de borrarlos — borrar
  el código hubiera ocultado, en el caso de `QA-003`, que la selección de perfil de umbral es una
  funcionalidad que la especificación da por hecha y el código no tiene.

Nunca lanzados, entre ellos: ~~`AMCCA-AI-005`~~ (techo de coste de agente excedido) y `AMCCA-JOB-001`
(*lease* expirado, token de vallado obsoleto, trabajo
abandonado).

**`AMCCA-AI-005` investigado, sin defecto que corregir en el código:** su propio comentario en
`AmccaErrors.cs` decía "timeout or max_cost ceiling" (dos condiciones en un código), pero SPEC/05 solo
documentaba la mitad de coste. Revisadas ambas mitades en `AgentRuntime.ExecuteToolCallAsync`:
- El techo de coste ya lo lanza `AMCCA-BUD-002` (vía su alias `Cst002`) en los dos puntos donde se
  comprueba (DEF-004) — `AI-005` es un duplicado nunca alcanzado, no una implementación pendiente.
- El timeout se aplica con un `CancellationTokenSource.CancelAfter` y se deja propagar como
  `OperationCanceledException` sin envolver — comprobado que esto es un contrato ya probado
  deliberadamente (`TimeoutSeconds_CancelsExecutionWhenExceeded` y otro test en
  `AgentCostReservationOrderRegressionTests`), y es la convención correcta de .NET para cancelación.
  Envolverlo en `AmccaException` sería una regresión, no una corrección.

Catalogado con nota explícita en `SPEC/05` y `AmccaErrors.cs` en vez de borrado, mismo tratamiento que
`AMCCA-QA-003`/`AMCCA-RES-003`/`AMCCA-JOB-002`. No se tocó ningún test ni código de `AgentRuntime`.

**Resuelto para `AMCCA-JOB-001`, y con un defecto real detrás:** no solo nunca se lanzaba — donde debía
lanzarse, el código lanzaba `AMCCA-JOB-003` en su lugar. `CompleteJobOrThrowAsync` y la sobrecarga de
`FailJobAsync` con comprobación de *fence token* rechazaban un token obsoleto con `AMCCA-JOB-003`
(`USER_ACTION_REQUIRED`, "job en dead-letter tras máximos intentos") cuando la condición real es la que
`AMCCA-JOB-001` describe literalmente: un worker cuyo *lease* ya pasó a otro dueño, que debe abandonar sin
que ningún operador tenga que intervenir (`TRANSIENT`, reintentable por quien sí sostiene el *lease*).
Corregidos ambos puntos a `AMCCA-JOB-001`/`TRANSIENT`. `RequeueDeadLetterJobAsync` sigue usando
`AMCCA-JOB-003` para su propio caso (un operador intenta reencolar un job que no está en `DEAD_LETTER`),
que sí encaja con la categoría — no se tocó.

**Un tercer punto igual de mal codificado, encontrado al corregir los dos anteriores:**
`HeartbeatLeaseOrThrowAsync` lanzaba `AMCCA-JOB-002` ("clave de idempotencia duplicada" según SPEC/05)
para exactamente la misma condición de *fence token* obsoleto — el propio comentario del código ya
delataba la confusión ("expired lease or duplicate key", mezclando dos cosas no relacionadas). Un
*heartbeat* es otra forma de escritura que un worker obsoleto debe abandonar, así que es el mismo caso que
`AMCCA-JOB-001` — corregido igual. `AMCCA-JOB-002` queda declarado pero sin uso real: `EnqueueJobAsync` no
comprueba la clave de idempotencia duplicada antes de insertar, así que hoy una violación de
`UNIQUE(idempotency_key)` sale como una excepción de base de datos sin envolver, no como este código. Se
cataloga con nota explícita en `SPEC/05`, igual que `AMCCA-QA-003` — no se implementó la comprobación
porque hacerlo bien exige capturar y distinguir el tipo de excepción real de SQLite, algo que no puedo
verificar sin poder compilar en este entorno; inventarlo sin poder probarlo sería peor que dejarlo
documentado como hueco real.

`CompleteJobAsync` (la sobrecarga que devuelve `bool` en vez de lanzar) sigue devolviendo `false` sin
código ante un *fence token* obsoleto; se deja así deliberadamente porque es un contrato alternativo ya
cubierto por tests explícitos (`JobsAndLeasesContractTests`, `ConcurrencySuiteSpec73Tests`) y no está en
la ruta de ningún llamador de producción — cambiar su forma de señalar el fallo sin que nadie lo consuma
sería adivinar un requisito, no corregir uno.

**SPEC/14 también decía algo que el código nunca hizo:** "on exhaustion the job moves to `DEAD_LETTER`
with `AMCCA-JOB-003` and a notification" — la transición a `DEAD_LETTER` era un cambio de estado
silencioso, sin código adjunto ni notificación (`JobManager` no tiene ninguna dependencia capaz de emitir
una; el Core no depende del `INotificationService` de WPF, correctamente, y no existe todavía un rastro de
auditoría del ciclo de vida de un job que pudiera alimentar una notificación por otra vía). Se añadió
`JobQueueEntry.ReasonCode` (calculado, igual que `IsDeadLettered`: `AMCCA-JOB-003` si y solo si
`state = DEAD_LETTER`) y una columna "Reason Code" en Job Queue, satisfaciendo la obligación 6 de SPEC/60
sin inventar un mecanismo de notificación que no existe. Se corrigió la redacción de SPEC/14 para
describir esto con precisión en vez de prometer una notificación push que ningún operador recibirá si no
tiene Job Queue abierto en el momento.

### 2.5 Contratos incompletos (decisiones que el código debe tomar y la SPEC no fija)

| Punto | Documento | Qué falta |
|---|---|---|
| ~~Rango de versión de FFmpeg soportado~~ **Resuelto** | SPEC/49 gate 8 | El código ya solo comprueba presencia + `-version` ejecutable, sin rango (correcto: inventar un rango no verificado violaría «no inventar capacidades»). El texto de SPEC/49 decía «within the supported range» sin definirlo nunca; se corrigió la redacción de la gate 8 para describir lo que el código realmente hace, y se añadió una nota explicando por qué no hay rango y que una incompatibilidad real debe aparecer como fallo de render (`AMCCA-MED-001`/`002`), no como bloqueo de arranque. |
| ~~Semántica del contador de intentos al reencolar un dead-letter~~ **Resuelto** | SPEC/14 | El código (`JobManager.RequeueDeadLetterJobAsync`) ya preservaba el contador deliberadamente (lectura acotada: un intento más, no un presupuesto nuevo) pero SPEC/14 no lo decía. Se añadió el párrafo a SPEC/14 ("Retries and dead-lettering") codificando esa decisión, y se simplificó el comentario del código para referenciarla en vez de repetir el razonamiento. |
| ~~Ruta del `config.yaml` desplegado~~ **Resuelto** | SPEC/03, DECISIONS | `App.xaml.cs` ya resolvía `%LocalAppData%\AMCCA\config.yaml` (junto a `amcca.db`), con su propio comentario justificando la elección. No era una decisión sin tomar, era una decisión tomada y no documentada. Se añadió la sección "Deployed configuration file location" a SPEC/03 en vez de abrir un ADR nuevo en `DECISIONS.md` — es una aclaración de un comportamiento ya implementado, no una decisión arquitectónica nueva que requiera bump de `PACKAGE_VERSION`. |

---

## 3. Hallazgos sobre la implementación

### 3.1 SPEC/60 declara obligaciones normativas incumplidas

SPEC/60 («Desktop Control Center») no es estilístico: dice «*These are normative, not stylistic*».
Estado real tras el trabajo P0:

| # | Obligación | Estado |
|---:|---|---|
| — | «*The UI thread performs no I/O, no database access and no waiting*» | ❌ el arranque bloquea el hilo de UI sobre el preflight |
| 1 | Kill switch alcanzable en una acción **desde cada pantalla** | ✅ **Resuelto** (`ToggleKillSwitchCommand`/`IsKillSwitchActive` en el chrome compartido de `MainWindow.xaml`, corregido esta sesión: fila desactualizada) |
| 2 | Modo de autonomía y estado de publicación visibles en cada pantalla | ✅ **Resuelto** (`AutonomyMode`/`PublishingEnabled` en el mismo chrome compartido, corregido esta sesión: fila desactualizada) |
| 3 | Todo número lleva su procedencia; medido y estimado visualmente distintos | ❌ no implementado |
| 4 | Todo elemento bloqueado indica qué regla lo bloqueó, de qué versión de política y qué lo desbloquearía | ❌ no implementado |
| 5 | Toda solicitud de aprobación muestra acción, **sujeto, techo de coste y expiración** | ✅ **Resuelto** |
| 6 | Ninguna pantalla muestra un fallo sin código de error y acción del operador | 🟡 solo en el requeue de Job Queue |
| 7 | Operaciones largas muestran progreso y son cancelables | ❌ no implementado |

**Corregido esta sesión (revisión de la propia auditoría, no trabajo nuevo):** la fila de la obligación 5
estaba desactualizada — `ApprovalManager.GetPendingApprovalsAsync` ya deserializa `scope_json` a
`ApprovalScope` (sujeto, techo de coste) y lo expone en `PendingApproval`; `ApprovalQueueViewModel`
proyecta eso en `ApprovalItem.SubjectDisplay`/`CostCeilingDisplay` (con el texto explícito
`"(no scope recorded)"` para una aprobación heredada o sin scope, en vez de una celda en blanco);
`ApprovalQueueView.xaml` tiene columnas de grid para `Action`, `SubjectDisplay`, `CostCeilingDisplay` y
`ExpiresAt`. `spec60.obligation_5_approval_detail_columns` (`validate_package.py`) verifica exactamente
esos cuatro bindings y pasa en verde. Cubierto además por
`ApprovalQueueViewModel_ExposesScopeSubjectCostCeilingAndExpiry` en `WpfMvvmContractTests.cs`, que
comprueba tanto el caso con scope como el caso heredado sin scope. Un operador ya no aprueba a ciegas.

### 3.2 El Production Inspector cubre aproximadamente la mitad de lo que SPEC/60 exige — **Resuelto salvo la oportunidad (subsistema inexistente)**

SPEC/60 lo llama «*the most important screen*» y enumera su contenido obligatorio:

| Exigido | Presente |
|---|---|
| Historial de transiciones con transition ids | ✅ |
| Todo evento de coste | ✅ |
| ~~Toda publicación **con su evidencia**~~ | ✅ **Resuelto**: ahora muestra `evidence_source`/`evidence_retrieved_at`, no solo la URL |
| Oportunidad y desglose de su puntuación | ❌ (subsistema inexistente, sin cambios — ver más abajo) |
| ~~Claims con fuentes y marcas de tiempo de recuperación~~ | ✅ **Resuelto** |
| ~~DAG de artefactos con estados de versión~~ | ✅ **Resuelto**: pestaña nueva con las aristas de `artifact_edges` (antes solo se veían los nodos) |
| ~~Todo hallazgo de QA con su nodo responsable~~ | ✅ **Resuelto**: pestaña nueva sobre `qa_findings` (antes solo se veían los `qa_reports` agregados) |
| ~~Toda decisión de política~~ | ✅ **Resuelto**: pestaña nueva sobre `policy_decisions` |

**Corregido esta sesión:** se añadieron cuatro pestañas nuevas (Claims, Policy Decisions, QA Findings,
Artifact DAG) y se completaron las columnas de evidencia de Publications, todo sobre datos que ya
existían en las tablas correspondientes — ninguna requirió una migración ni un escritor nuevo, solo la
lectura que faltaba en `ProductionInspectorViewModel`. `claims` se muestra una fila por par
claim-fuente (`LEFT JOIN` contra `claim_sources`/`sources`, para que una claim sin fuente todavía no
desaparezca de la vista); `qa_findings` y `artifact_edges` no tienen `production_id` propio, así que se
unen a través de `qa_reports`/`artifact_versions`+`artifacts` respectivamente para acotarlos a la
producción seleccionada. Cubierto por una extensión de
`ProductionInspectorViewModel_LoadsFullAggregateForSelectedProduction` en `WpfMvvmContractTests.cs`.

La fila de **oportunidad y desglose de puntuación** sigue sin resolver deliberadamente: `productions.opportunity_id`
existe, pero no hay ninguna tubería real que genere y puntúe oportunidades todavía (la tabla `opportunities`
no tiene un escritor de producción en `src/AMCCA.Core`) — mostrar esa pestaña hoy sería una UI para datos
que nunca van a aparecer, no una funcionalidad pendiente de cablear. Se deja fuera por el mismo principio
que ya rige el resto del proyecto: no inventar una capacidad que el sistema no tiene todavía.

### 3.3 Ruta de actualización del kill switch — **Resuelto (migración 005)**

`SettingsViewModel` escribía antes en `settings['kill_switch.global']`; ahora persiste en
`kill_switch_state`, que es lo que lee el gate 10 del preflight. La clave antigua quedaba **sin escritor
y sin lector**: una instalación existente con el kill switch activado lo habría perdido silenciosamente
al actualizar.

**Corregido esta sesión (revisión de la propia auditoría, no trabajo nuevo):** esta sección estaba
desactualizada. La migración 005 (`005_migrate_kill_switch_from_settings_table`) ya copia el valor de
`settings['kill_switch.global']` a `kill_switch_state`, condicionado a `NOT EXISTS` para no pisar una
decisión que la instalación ya hubiera tomado en la tabla nueva, y borra la clave antigua tras
migrarla. La regresión de seguridad en el camino de actualización ya no existe.

---

## 4. Lo que el gate de release no comprueba

El gate está bien construido, y precisamente por eso conviene nombrar sus puntos ciegos, todos
confirmados por los hallazgos anteriores:

1. ~~No compara `enum` de contrato contra `CHECK` del DDL~~ **Resuelto** (`contracts.enum_matches_ddl_check`) → §2.1 y §2.2 pasaron a ser fallos visibles (las 16 columnas sin CHECK y las 2 que divergían), y **el propio check está ahora en verde**: las 17 columnas (16 de §2.1 más `cost_events.kind` de §2.2; `publications.state` resuelto en la misma migración) tienen su `CHECK` real, verificado contra SQLite real (no solo contra el harness simplificado del propio check) por la dificultad añadida de que cinco de esas tablas son objetivo de FK — ver el detalle en §2.1.
2. ~~No comprueba que los códigos lanzados por el código estén catalogados~~ **Resuelto**
   (`refs.all_thrown_error_codes_catalogued`, con mutation test `mutation_17`) → resuelve cada
   `AmccaErrors.Xxx` que aparece en un `throw new AmccaException(...)` real del código (incluyendo el alias
   `Cst002 = Bud002`) a su valor `"AMCCA-..."` literal y exige que esté catalogado en `SPEC/05`, en vez de
   solo escanear citas en prosa `.md` como hacía `refs.all_error_codes_catalogued`.
3. ~~No comprueba las obligaciones normativas de SPEC/60~~ **Parcialmente resuelto**
   (`spec60.obligation_1_kill_switch_in_shared_chrome`, `spec60.obligation_2_autonomy_and_publishing_visible`,
   `spec60.obligation_5_approval_detail_columns`, `spec60.obligation_6_no_bare_generic_failure_text`, con
   mutation test `mutation_18`) → §3.1 deja de pasar en verde de forma incondicional para las obligaciones
   1, 2 y 5, que ya tenían una firma textual concreta y verificable en el build de seis pantallas (el
   binding del kill switch y de autonomía/publicación en `MainWindow.xaml`, las columnas de
   `ApprovalQueueView.xaml`), más una comprobación parcial de la obligación 6 (que ningún fichero de
   `src/AMCCA.App` contenga una frase de fallo genérico tipo "algo salió mal"). No se intentó un verificador
   general de "el kill switch es alcanzable desde cada pantalla": eso no es un grep, es una propiedad de
   comportamiento en tiempo de ejecución. Lo que se comprueba es la firma concreta que ya existe, así que
   una regresión que la elimine se vuelve un fallo del gate en vez de un cambio silencioso — no más ni
   menos que eso.
4. ~~No detecta campos de contrato sin columna~~ **Resuelto y en verde de punta a punta**
   (`contracts.fields_have_columns`, con mutation test `mutation_19`) → de los 20 campos que este check
   sacó a la luz: `referral_links.brand`/`commission_model`/`disclosure_required` resultaron ser diseño
   correcto (normalizados en `referral_programs`, no un hueco); `cost_events.units` era un falso positivo
   del propio check (objeto anidado envuelto en `oneOf`, corregido con `_is_nested_shape()`); y los 16
   restantes eran huecos reales, cerrados en tres migraciones (007, 008, 009) — el ejemplo original de la
   auditoría, `cost_events.reconciliation_state`, era solo uno de ocho campos sin columna en esa misma
   tabla. `contracts.fields_have_columns` pasa en verde por primera vez desde que existe.

Los cuatro puntos ciegos que esta auditoría nombró están ahora cerrados como checks del gate. Las
obligaciones 3, 4 y 7 de
SPEC/60, y la otra mitad de la 6 (que todo fallo real muestre su código de SPEC/05, no solo que no aparezca
una frase prohibida), siguen sin comprobación mecánica posible sin ejecutar la UI — documentarlas como
"comprobadas" sería exactamente el tipo de gate que pasa en verde sin significar nada, que es lo que esta
sección entera existe para evitar.

---

## 5. Estado del trabajo P0 de esta rama

Verificado mecánicamente: **el conjunto de fallos del `release_gate.py` en esta rama es idéntico al de
`origin/main`** salvo por uno que esta rama corrige (higiene de repositorio). Cero regresiones
introducidas en la superficie cubierta por la herramienta.

| P0 | Estado real |
|---|---|
| Preflight completo | Cerrado (10 gates, invocado en arranque) |
| Eliminar SQL directo de la UI | Cerrado para mutaciones; Dashboard y Audit Log siguen leyendo SQL directo |
| Approval Queue vía dominio | Cerrado en la ruta; **cumple SPEC/60 obl. 5** (§3.1, corregido esta sesión: la auditoría estaba desactualizada) |
| Kill switch operacional | Cerrado, incluida la ruta de actualización (§3.3) y la obl. 1 (§3.1), corregido esta sesión: fila desactualizada |
| Production Inspector | Completo salvo oportunidad/puntuación (subsistema inexistente, §3.2) |
| Job Queue | Cerrado, incluido el requeue de dead-letter que SPEC/14 exigía y no existía |
| Product E2E | Parcial: recorre las capas reales; el viaje completo depende de subsistemas inexistentes |

---

## 6. Prioridades recomendadas

| Prioridad | Acción | Fundamento |
|---|---|---|
| P0 | Compilar y ejecutar la suite .NET | Nada de esta rama ha sido compilado (§0) |
| P0 | ~~Resolver `audit_log.actor_type` contrato ↔ DDL~~ **Ya resuelto** (migración 004; entrada de la auditoría desactualizada, corregido esta sesión) | El orquestador ya puede auditarse a sí mismo (§2.2) |
| P0 | ~~Acotar `tool_runs.side_effect_class` con `CHECK`~~ **Resuelto** (migración 010) | La defensa de intent ya no falla en abierto (§2.1) |
| P1 | ~~Añadir al gate la comparación enum ↔ `CHECK`~~, ~~códigos lanzados ↔ catálogo~~, ~~firmas de obligaciones 1/2/5/6 de SPEC/60~~ y ~~campos de contrato ↔ columna~~ **Resueltos** | Convierte §2.1–2.3, §3.1 y los 4 puntos ciegos de §4 en fallos visibles |
| P2 | ~~13 campos de contrato sin columna (`cost_events`×6, `jobs`×7)~~ **Todos resueltos** (migración 009; `pricing_snapshot_id` deliberadamente nulable, ver §2.2) | `contracts.fields_have_columns` en verde |
| P2 | ~~`analytics_snapshots.source_account_id`~~ **Resuelto** (migración 007); ~~`cost_events`/`jobs`.`schema_version`~~ **Resuelto** (migración 008, D-004); ~~`referral_links.brand`/`commission_model`/`disclosure_required`~~ **no eran un hueco** (normalizados en `referral_programs`) | §2.2 |
| P1 | Catalogar los 6 códigos de error huérfanos | Obligación 6 de SPEC/60 (§2.4) |
| P1 | ~~Aprobaciones: mostrar sujeto, techo de coste y expiración~~ **Ya resuelto** (entrada de la auditoría desactualizada, corregido esta sesión) | Obligación 5, `spec60.obligation_5_approval_detail_columns` en verde (§3.1) |
| P1 | ~~Resolver `COMPLETED` vs `SUCCEEDED`~~ **Ya resuelto** (migración 006; entrada de la auditoría desactualizada, corregido esta sesión) | Contradicción contrato ↔ implementación (§2.3) |
| P2 | ~~Completar el Inspector con claims, decisiones de política, hallazgos de QA y aristas del DAG~~ **Resuelto** (evidencia de publicaciones incluida); oportunidad/puntuación queda fuera a propósito, subsistema inexistente | §3.2 |
| P2 | ~~Migración del kill switch desde `settings`~~ **Resuelto** (migración 005) | Regresión en actualización (§3.3) |
| P2 | ~~Fijar rango de FFmpeg~~, ~~semántica de requeue~~ y ~~ruta de config~~ **Resueltos**, ver §2.5 | Contratos incompletos (§2.5) |
| P2 | ~~Kill switch y modo de autonomía en todas las pantallas~~ **Resuelto** | Obligaciones 1 y 2 (§3.1) |

---

## 7. Veredicto

El Core sigue siendo sólido y el gate de release es bueno. Lo que esta auditoría añade a la anterior es
que **una parte de la confianza que da el verde del gate no está justificada**: hay 19 divergencias
entre contratos y esquema físico, y 6 códigos de error en uso fuera del catálogo, que el gate no puede
ver por construcción. La brecha ya no es solo «falta producto»; es que **los contratos y su aplicación
han divergido sin que nada lo señale**.

Y la conclusión más incómoda es de método: el documento que ha dirigido la remediación tiene rota la
trazabilidad al corpus normativo en 68 de 83 entradas. Los planes derivados de él deben re-anclarse a
los ficheros SPEC reales antes de continuar.
