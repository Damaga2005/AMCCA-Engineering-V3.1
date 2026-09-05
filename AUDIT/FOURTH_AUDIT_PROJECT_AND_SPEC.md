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

### 2.1 Contratos declarados pero no aplicados (16 columnas)

`validate_package.py` comprueba que toda tabla tenga contrato (`db.every_table_has_contract`, PASS),
pero **no compara los `enum` del contrato con las restricciones `CHECK` del DDL**. Comparándolos:

| Columna | Contrato | DDL |
|---|---:|---|
| `productions.state` | 32 valores | **sin `CHECK`** |
| `jobs.state` | 9 valores | **sin `CHECK`** |
| `tool_runs.state` | 6 valores | **sin `CHECK`** |
| `tool_runs.side_effect_class` | 5 valores | **sin `CHECK`** |
| `agent_runs.state` | 7 valores | sin `CHECK` |
| `events.aggregate_type` | 9 valores | sin `CHECK` |
| `audit_log.outcome` | 6 valores | sin `CHECK` |
| `productions.autonomy_mode` | 3 valores | sin `CHECK` |
| `qa_reports.stage`, `claims.materiality`, `claims.subject_class`, `rights_records.provenance`, `rights_records.commercial_use`, `rights_records.modification`, `referral_links.validation_method`, `analytics_snapshots.provenance` | — | sin `CHECK` |

Esto contradice el principio que el propio proyecto declara en D-026: *«This gate is **structural**, not
merely procedural… This holds even if the preflight code path that is supposed to enforce it has a
bug»*. El proyecto cree en la aplicación estructural pero solo la aplica en algunas columnas.

Dos casos merecen atención especial:

- **`productions.state` sin `CHECK`** es la máquina de estados central del sistema sin ninguna defensa
  a nivel de base de datos. Solo el código de aplicación la protege.
- **`tool_runs.side_effect_class` sin `CHECK`** es relevante para seguridad. Existe la restricción
  condicional `CHECK(side_effect_class <> 'EXTERNAL_UNSAFE' OR intent_id IS NOT NULL)`, pero como el
  dominio de valores no está acotado, un valor mal escrito (`'external_unsafe'`, espacio final) **evade
  la exigencia de intent** en lugar de ser rechazado. La defensa estructural falla en abierto.

### 2.2 Contradicciones directas contrato ↔ DDL (3)

| Columna | Contrato permite y el DDL rechaza | DDL permite y el contrato rechaza |
|---|---|---|
| `audit_log.actor_type` | `ORCHESTRATOR`, `RECONCILER`, `SCHEDULER` | — |
| `cost_events.kind` | `ESTIMATE`, `RELEASE` | `REFUND` |
| `publications.state` | — | `QUEUED`, `RECONCILING`, `RETRACTED`, `SUBMITTED` |

La primera es la más grave: `audit.schema.json` admite cinco tipos de actor y el DDL solo dos
(`'OPERATOR','SYSTEM'`). SPEC/12 designa al **orquestador** como único committer de estado; si algún
día escribe su propia entrada de auditoría con `actor_type='ORCHESTRATOR'` —que el contrato autoriza
explícitamente— **la base de datos la rechazará**.

Además, `cost_events.reconciliation_state` está declarado en el contrato y **la columna no existe** en
el DDL: un campo de contrato sin almacenamiento.

### 2.3 Contradicción contrato ↔ implementación

`job.schema.json` enumera `SUCCEEDED`; `JobManager.CompleteJobAsync` escribe `COMPLETED`. Como
`jobs.state` no tiene `CHECK` (§2.1), nada lo detecta. Un test existente afirma `COMPLETED`, con lo que
la implementación y su test están de acuerdo entre sí y en desacuerdo con el contrato.

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
| 1 | Kill switch alcanzable en una acción **desde cada pantalla** | ❌ solo en Settings |
| 2 | Modo de autonomía y estado de publicación visibles en cada pantalla | ❌ no implementado |
| 3 | Todo número lleva su procedencia; medido y estimado visualmente distintos | ❌ no implementado |
| 4 | Todo elemento bloqueado indica qué regla lo bloqueó, de qué versión de política y qué lo desbloquearía | ❌ no implementado |
| 5 | Toda solicitud de aprobación muestra acción, **sujeto, techo de coste y expiración** | ❌ la cola muestra Id, Producción, Acción, Estado, Creado |
| 6 | Ninguna pantalla muestra un fallo sin código de error y acción del operador | 🟡 solo en el requeue de Job Queue |
| 7 | Operaciones largas muestran progreso y son cancelables | ❌ no implementado |

La obligación 5 afecta directamente a la corrección de DEF-002: la cola de aprobaciones pasa ahora por
el dominio, pero **sigue sin mostrar al operador el alcance que está aprobando**. El `scope_json`
contiene sujeto y techo de coste y no se lee. Un operador aprueba a ciegas.

### 3.2 El Production Inspector cubre aproximadamente la mitad de lo que SPEC/60 exige

SPEC/60 lo llama «*the most important screen*» y enumera su contenido obligatorio:

| Exigido | Presente |
|---|---|
| Historial de transiciones con transition ids | ✅ |
| Todo evento de coste | ✅ |
| Toda publicación **con su evidencia** | 🟡 muestra URL, no `evidence_source`/`evidence_retrieved_at` |
| Oportunidad y desglose de su puntuación | ❌ (subsistema inexistente) |
| Claims con fuentes y marcas de tiempo de recuperación | ❌ **datos existen en BD** |
| DAG de artefactos con estados de versión | 🟡 artefactos y versiones sí; aristas del DAG no |
| Todo hallazgo de QA con su nodo responsable | ❌ muestra informes, no `qa_findings` |
| Toda decisión de política | ❌ **tabla `policy_decisions` existe y no se lee** |

Cuatro de las ausencias tienen tabla y datos disponibles hoy: claims/fuentes, decisiones de política,
hallazgos de QA y aristas del DAG. Se construyó contra la descripción de una línea del informe anterior
en lugar de contra esta lista normativa.

### 3.3 Ruta de actualización del kill switch

`SettingsViewModel` escribía antes en `settings['kill_switch.global']`; ahora persiste en
`kill_switch_state`, que es lo que lee el gate 10 del preflight. La clave antigua queda **sin escritor y
sin lector**. Una instalación existente con el kill switch activado **lo perdería silenciosamente al
actualizar**. Requiere una migración que copie el valor. Riesgo real bajo (la aplicación probablemente
nunca se ha desplegado), pero es una regresión de seguridad en el camino de actualización.

---

## 4. Lo que el gate de release no comprueba

El gate está bien construido, y precisamente por eso conviene nombrar sus puntos ciegos, todos
confirmados por los hallazgos anteriores:

1. ~~No compara `enum` de contrato contra `CHECK` del DDL~~ **Resuelto** (`contracts.enum_matches_ddl_check`) → §2.1 y §2.2 ahora son fallos visibles (las 16 columnas sin CHECK y las 2 que divergen).
2. ~~No comprueba que los códigos lanzados por el código estén catalogados~~ **Resuelto**
   (`refs.all_thrown_error_codes_catalogued`, con mutation test `mutation_17`) → resuelve cada
   `AmccaErrors.Xxx` que aparece en un `throw new AmccaException(...)` real del código (incluyendo el alias
   `Cst002 = Bud002`) a su valor `"AMCCA-..."` literal y exige que esté catalogado en `SPEC/05`, en vez de
   solo escanear citas en prosa `.md` como hacía `refs.all_error_codes_catalogued`.
3. No comprueba las obligaciones normativas de SPEC/60 → §3.1 pasa en verde.
4. No detecta campos de contrato sin columna → `cost_events.reconciliation_state` pasa en verde.

Las dos restantes son automatizables igualmente. La tercera (obligaciones normativas de SPEC/60) es la de
mayor rendimiento pendiente: convertiría §3.1 en fallos visibles en lugar de deuda invisible, pero requiere
antes decidir cómo verificar mecánicamente algo tan poco estructurado como "el kill switch es alcanzable
desde cada pantalla" — no es un simple grep ni una comparación de esquemas.

---

## 5. Estado del trabajo P0 de esta rama

Verificado mecánicamente: **el conjunto de fallos del `release_gate.py` en esta rama es idéntico al de
`origin/main`** salvo por uno que esta rama corrige (higiene de repositorio). Cero regresiones
introducidas en la superficie cubierta por la herramienta.

| P0 | Estado real |
|---|---|
| Preflight completo | Cerrado (10 gates, invocado en arranque) |
| Eliminar SQL directo de la UI | Cerrado para mutaciones; Dashboard y Audit Log siguen leyendo SQL directo |
| Approval Queue vía dominio | Cerrado en la ruta; **incumple SPEC/60 obl. 5** (§3.1) |
| Kill switch operacional | Cerrado funcionalmente; sin ruta de actualización (§3.3); incumple obl. 1 |
| Production Inspector | Parcial: ~50 % de SPEC/60 (§3.2) |
| Job Queue | Cerrado, incluido el requeue de dead-letter que SPEC/14 exigía y no existía |
| Product E2E | Parcial: recorre las capas reales; el viaje completo depende de subsistemas inexistentes |

---

## 6. Prioridades recomendadas

| Prioridad | Acción | Fundamento |
|---|---|---|
| P0 | Compilar y ejecutar la suite .NET | Nada de esta rama ha sido compilado (§0) |
| P0 | Resolver `audit_log.actor_type` contrato ↔ DDL | El orquestador no puede auditarse a sí mismo (§2.2) |
| P0 | Acotar `tool_runs.side_effect_class` con `CHECK` | La defensa de intent falla en abierto (§2.1) |
| P1 | ~~Añadir al gate la comparación enum ↔ `CHECK`~~ y ~~códigos lanzados ↔ catálogo~~ **Resueltos** | Convierte §2.1–2.3 y el punto ciego 2 de §4 en fallos visibles |
| P1 | Catalogar los 6 códigos de error huérfanos | Obligación 6 de SPEC/60 (§2.4) |
| P1 | Aprobaciones: mostrar sujeto, techo de coste y expiración | Obligación 5, operador aprobando a ciegas (§3.1) |
| P1 | Resolver `COMPLETED` vs `SUCCEEDED` | Contradicción contrato ↔ implementación (§2.3) |
| P2 | Completar el Inspector con claims, decisiones de política, hallazgos de QA y aristas del DAG | Datos ya disponibles (§3.2) |
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
