# Sexta auditoría — solo código

**Fecha:** 2026-09-07
**Base de comparación:** `main` @ `34c3cb8` (cert quinta auditoría) → `bf8fb50` (cert vigente)
**Alcance:** el *delta* de `src/` desde la quinta auditoría — PR #6 (bootstrap local + D-036),
PR #7 (resiliencia del orquestador), PR #8 (D-037 reasoning-model), PR #9 (*fixes* del run real
contra `gpt-4o`). 15 ficheros, +755 / −346. El resto del árbol quedó cubierto por
`FIFTH_AUDIT_CODE.md` y no ha cambiado.

Cada hallazgo respaldado por lectura del árbol en `bf8fb50`. Ninguno bloquea la certificación
vigente; son deuda introducida por el trabajo de "conectar un proveedor real" hecho bajo presión.

---

## 1. Hallazgos

Los seis cerrados en PR #10 (`fix/sixth-audit-findings`).

| # | Sev | Hallazgo | Cierre |
|---|-----|----------|--------|
| **S1** | Med | `--probe` promueve `autonomy_mode: ASSISTED → AUTONOMOUS` como efecto colateral de un *probe* de capacidad. | `--probe` sólo toca `capabilities_verified` (evidencia D-028); si `autonomy_mode` sigue `ASSISTED` lo *dice* y el operador lo cambia a mano. Regex anclado (`^\s*`, `Multiline`) para no casar líneas comentadas. |
| **S2** | Med | Los verbos de bootstrap se compilan sin guardas en el `.exe` de release; `--seed-demo` escribe filas demo en la BD real, `--probe` reescribe el `config.yaml`. | `Program.cs`: en build no-`DEBUG` los verbos exigen `AMCCA_ALLOW_BOOTSTRAP=1` o salen con código 2 sin tocar nada. |
| **S3** | Med | El cuerpo de error HTTP crudo del proveedor llega al *detail* de `ORC-002`, al log Serilog y al mensaje de `BrokenCircuitException`. Incoherente con SEC-10. | `ProviderErrorText.Sanitize`: redacta la key resuelta + todo lo con forma de *bearer* / `sk-` / `api_key=`, y corta a 300 car. Aplicado en el adaptador *Direct* y en `ResilientProviderGateway`. |
| **S4** | Low | D-037 `reasoning_model` sólo se cablea al adaptador *Direct*; `id: omnirouters` lo ignora en silencio. Sin regla que ligue `reasoning_model: true` a `default_model_id`. | `OmniRoutersGatewayAdapter` recibe `reasoningModel` y omite `temperature` igual que el *Direct*. `ConfigService` Regla 6: `reasoning_model: true` sin `default_model_id` → `Cfg004`. |
| **S5** | Low | `OmniRoutersGatewayAdapter.cs` con *flip* LF→CRLF de todo el fichero (`.gitattributes` `* -text` lo hace permanente). | `dos2unix` en el mismo PR — coste único de *diff*, a partir de aquí LF. |
| **S6** | Low | `AgentTranscriptLog.Write` (clave `stage+productionId`, `WriteAllText`) → un *rework* pisa el *transcript* del run fallido. | Sufijo `-{yyyyMMdd-HHmmss}` en el nombre; cada run deja su fichero. |

---

## 2. Detalle

### S1 — `--probe` escala la autonomía del sistema (Med)

`ProbeAsync` (`LocalRunCli.cs:139-148`), tras un *probe* de texto exitoso, hace **dos** cambios en
`config.yaml` por regex:

```
capabilities_verified: false -> true      # defendible: el probe ES la evidencia que pide D-028
autonomy_mode: ASSISTED     -> AUTONOMOUS  # decisión de política, no de capacidad
```

D-028 sólo exige `capabilities_verified: true` para **permitir** AUTONOMOUS. Elegir AUTONOMOUS
—dejar que la máquina gaste dinero de IA y publique sin parar en cada gate— es una decisión del
operador, y un único *probe* de texto verde es evidencia fina para tomarla por él. El nombre del
verbo (`--probe`) no anuncia esta mutación.

**Arreglo:** quitar el *flip* de `autonomy_mode`; dejar sólo `capabilities_verified`. Si se quiere el
atajo, ponerlo tras un flag explícito (`--probe --go-autonomous`) o un verbo aparte.

Sub-punto: los regex son ingenuos (`capabilities_verified\s*:\s*false` casa también una línea
comentada `# capabilities_verified: false`). Se opera por texto crudo sobre el fichero, no
re-serializando el `AmccaConfig` ya parseado en la línea 108. Frágil, aunque acotado a una
herramienta de dev.

### S2 — verbos de bootstrap sin guarda en el binario de release (Med)

`Program.Main` (`Program.cs:42-45`) despacha `--set-secret` / `--probe` / `--seed-demo` antes de
`app.Run()`, sin `#if DEBUG`, sin `Debugger.IsAttached`, sin variable de entorno. El instalador WPF
de producción incluye estos verbos. `AMCCA.exe --seed-demo` sobre la máquina de un operador:

- corre `MigrationService.UpgradeAsync()` sobre la BD real que resuelve `Composition.ResolvePaths()`;
- inserta por SQL crudo (`c.ExecuteAsync`, `LocalRunCli.cs:173-181`) una `niche`, una `opportunity`
  `SCORED` con `score 0.85` / `expected_cost '3.000000'` inventados, una `production` AUTONOMOUS y un
  `budget` PRODUCTION de 25 €;
- transiciona la producción a `RESEARCHING`.

`--probe` reescribe el `config.yaml` del operador (S1). Ninguno es un adaptador de éxito falso ni un
agente escribiendo filas, así que no viola literalmente `CLAUDE.md`, pero es tooling de dev con
efectos de grado producción expuesto sin condición.

**Arreglo:** `#if DEBUG` sobre el bloque de `Program.cs:42`, o exigir `AMCCA_ALLOW_BOOTSTRAP=1`.
Como mínimo para `--seed-demo` y el *flip* de `autonomy_mode`.

### S3 — el cuerpo de error del proveedor entra en logs sin sanear (Med)

PR #7 cambió el error 4xx del gateway para incluir el cuerpo del proveedor
(`DirectOpenAiCompatibleGatewayAdapter.cs:255-261`, truncado a 400 car.):

```csharp
var body = await httpResponse.Content.ReadAsStringAsync(ct);
if (body.Length > 400) body = body.Substring(0, 400) + "…";
throw new AmccaException(Ai001, ErrorCategory.Provider,
    $"Model provider returned HTTP {(int)httpResponse.StatusCode}: {body}");
```

Ese mensaje se propaga: `OrchestratorEngine.cs:159-163` lo mete en el *detail* de `ORC-002`
(`ex.Message` + `InnerException.Message`), y `OrchestratorHostedService.cs:76-79` lo escribe con
`LogWarning` al log Serilog de fichero. `ResilientProviderGateway.cs:104` hace lo mismo con
`args.Outcome.Exception?.Message` al abrir el circuito.

El comentario del código afirma *"The body is the provider's JSON error, not our request, so it
carries no secret"*. Es una suposición, no una garantía: proveedores compatibles-OpenAI han devuelto
en el cuerpo de error fragmentos de la petición o cabeceras. `CLAUDE.md` — *"No guardar secretos en
logs"*. SEC-10 resolvió exactamente esto para OAuth: `SafeOAuthError` sólo repite un código
alfanumérico corto en *whitelist*, nunca el cuerpo. La ruta del proveedor de modelo ahora hace lo
contrario, sin equivalente.

Alcance real: sólo el log de fichero (el *detail* no se persiste en `audit_log` ni en el *event
store* — `OrchestratorAction.Detail` sólo lo consume el `HostedService`). Aun así el log es una
superficie nombrada explícitamente.

**Arreglo:** truncar más agresivo y/o pasar el cuerpo por un saneador (quitar todo lo que parezca
`sk-`, `Bearer `, `key=`), o registrar sólo `status + provider error code` como hace `SafeOAuthError`.

### S4 — D-037 medio cableado (Low)

`ProviderGatewayComposer.BuildAdapter` (`ProviderGatewayComposer.cs:49-52`):

```csharp
string.Equals(gw.Id, "omnirouters", ...)
    ? new OmniRoutersGatewayAdapter(gw.BaseUrl, secretStore, gw.ApiKeySecretRef!)     // sin gw.ReasoningModel
    : new DirectOpenAiCompatibleGatewayAdapter(gw.BaseUrl, secretStore, gw.ApiKeySecretRef!, gw.ReasoningModel);
```

`OmniRoutersGatewayAdapter` recibió el *fix* `max_tokens → max_completion_tokens` en PR #8 pero **no**
el parámetro `reasoningModel`. Con `id: omnirouters` + un modelo *reasoning*, `reasoning_model: true`
en config no hace nada y la petición sigue llevando `temperature` → HTTP 400. OmniRouters es el
*router* loopback (bloqueado por SSRF en la ruta de producción), así que el impacto es marginal, pero
es una opción de config que se ignora en silencio según el `id`.

Además: nada valida que `reasoning_model: true` venga con `default_model_id` no vacío. Sin
`default_model_id`, los agentes usan su constante interna (no-*reasoning*) y `reasoning_model` es un
*no-op* silencioso.

**Arreglo:** pasar `gw.ReasoningModel` también al constructor de `OmniRoutersGatewayAdapter`, o
documentar en el schema que `reasoning_model` no aplica a `omnirouters`. Regla de campo cruzado:
`reasoning_model` ⇒ `default_model_id` presente.

### S5 — *flip* de fin de línea en `OmniRoutersGatewayAdapter.cs` (Low)

Único fichero de `src/` con terminadores CRLF; el resto es LF. `.gitattributes` tiene `* -text`
(DEF-CERT-008, preservación de bytes) → git no lo normaliza nunca. `git diff 34c3cb8..bf8fb50` sobre
este fichero: `311 insertions(+), 310 deletions(-)` para un cambio de 3 líneas. Ruido de revisión
permanente hasta que alguien reconvierta el fichero a LF en un commit dedicado.

**Arreglo:** un commit único `dos2unix src/AMCCA.Core/Providers/OmniRoutersGatewayAdapter.cs`.
Verificar que ningún otro fichero nuevo (`LocalRunCli.cs`, `AgentTranscriptLog.cs`) haya entrado en
CRLF — a fecha de `bf8fb50`, ambos están en LF, OK.

### S6 — el *transcript* del agente se pisa en el reintento (Low)

`AgentTranscriptLog.Write` (`AgentTranscriptLog.cs:24,38`):

```csharp
var path = Path.Combine(dir, $"agent-{stage}-{productionId}.log");
...
File.WriteAllText(path, sb.ToString());
```

Clave = etapa + producción, y `WriteAllText` trunca. Si `RESEARCHING` falla, el `ResearchStageHandler`
enruta a *rework* y el siguiente *tick* vuelve a llamar al agente → el *transcript* del run que falló
(el que quieres leer) se sobrescribe con el del reintento. El propósito declarado del fichero
—"makes a stalled autonomous run undiagnosable" si no existe— se anula justo en el bucle de reintento.

**Arreglo:** sufijo con timestamp (`agent-{stage}-{productionId}-{yyyyMMddHHmmss}.log`) o modo *append*
con separador de run.

---

## 3. Revisión contra `CLAUDE.md` ("Never do")

| Regla | Veredicto |
|---|---|
| No inventar APIs / capacidades de proveedor | **OK.** `max_completion_tokens`, omisión de `temperature` y `reasoning_effort` (revertido) son campos reales de la API OpenAI; documentados en D-037. |
| No marcar éxito externo sin evidencia autoritativa | **OK.** El *probe* exige `result.Success` del gateway real antes de tocar el config. |
| No dejar que un agente escriba filas arbitrarias | **OK.** `--seed-demo` es un verbo de CLI operado a mano, no el agente. `AgentTranscriptLog` escribe a fichero, no a BD. |
| No guardar secretos en fuente / YAML / **logs** | **S3 cerrado.** `ProviderErrorText.Sanitize` redacta y corta el cuerpo antes de que entre en el mensaje de excepción — misma postura que `SafeOAuthError` (SEC-10). |
| No saltarse gates de QA / presupuesto / política para pasar una demo | **S1 cerrado.** `--probe` ya no elige la autonomía por el operador; sólo aporta la evidencia de capacidad que pide D-028. |
| No sustituir una integración que falla por un adaptador de éxito falso | **OK.** Los *fixes* de PR #7-#9 endurecen la ruta real (5xx reintentable, cuerpo de error visible, *nudge* de tools); ninguno finge éxito. |
| No usar coma flotante para dinero | **OK.** `--seed-demo` inserta `expected_cost '3.000000'` como TEXT; `budgets.CreateBudgetAsync(..., 25.000000m, ...)` es `decimal`. |
| No editar a mano un artefacto generado | **OK.** `config.schema.json` regenerado (`default_model_id`, `reasoning_model` presentes, líneas 198/203). MANIFEST regenerado en `9e3f3cb`. |
| No añadir framework / servicio sin ADR | **OK.** D-036 y D-037 añadidos a `DECISIONS.md`. |
| No resolver una contradicción eligiendo uno | **N/A** en este *delta*. |

---

## 4. Estado

Los seis cerrados en PR #10 (`fix/sixth-audit-findings`). Ninguno invalidaba `bf8fb50` por sí mismo,
pero el PR toca código → regla #4 de integridad → re-certificar `AUDIT/FINAL_RELEASE_CERTIFICATION.md`
sobre el commit de merge de PR #10.

**Tests:** `AiProviderRealIntegrationTests` +2 (`OmniRouters_OmitsTemperature_ForAReasoningModel`,
`ErrorBody_IsRedactedAndCapped_BeforeItReachesTheExceptionMessage`), `ConfigurationContractTests` +1
(`ReasoningModelWithoutDefaultModelId_AbortsWithCfg004`). Suite Core: `776 passed | 0 failed`.
Build Release: `0 warnings | 0 errors`. `validate_package.py`: 68/68. MANIFEST regenerado.

**Nota de contrato:** sin cambio de schema — `reasoning_model` ya estaba en `config.schema.json`
desde PR #8; S4 añade sólo una regla de validación de campo cruzado en código (Regla 6 de
`ValidateCrossFieldConsistency`). Addendum a D-037 en `DECISIONS.md`.
