# RUN_LOCAL — a real end-to-end run against Google Gemini

What this gets you: a production driven by the real orchestrator through
`INIT → RESEARCHING → RESEARCH_VERIFIED → CONCEPT_SELECTED → SCRIPTING → SCRIPT_VERIFIED`, with real
Gemini model calls, real web sources fetched by the research agent, a real SCRIPT artifact on disk, and
real `cost_events` rows. It then **BLOCKs at `STORYBOARDING`** — the media/publish stages have no
provider wired (by design; they block instead of faking).

Gemini is used through Google's OpenAI-compatibility endpoint, so no new adapter is needed. A localhost
router (OmniRoute at `localhost:20128`) will **not** work — the gateway's SSRF guard blocks loopback.

## Prerequisites

- Windows, .NET 8 SDK.
- A Google Gemini API key — <https://aistudio.google.com/apikey> (has a free tier).
- Build once:  `dotnet build src/AMCCA.App/AMCCA.App.csproj -c Release`
  The exe is then `src/AMCCA.App/bin/Release/net8.0-windows/AMCCA.exe` (called `AMCCA.exe` below). If the
  apphost cannot find the runtime in your shell, run `dotnet <that dir>/AMCCA.dll <args>` instead.

## Steps

**1. Config.** Copy the template to the data dir:

```bash
mkdir -p "$LOCALAPPDATA/AMCCA" && cp CONFIG/config.gemini.example.yaml "$LOCALAPPDATA/AMCCA/config.yaml"
```

Edit `default_model_id` if you want a different model. Optionally fill in `model_pricing` with Gemini's
current per-1M-token prices (otherwise cost rows are `ESTIMATED_UNRECONCILED` — honest, not zero).

**2. Store the key** (you type it; it goes straight into the DPAPI store, never an argument, never echoed):

```bash
AMCCA.exe --set-secret secret://amcca/gemini_api_key
```

**3. Probe** — a real `POST /chat/completions` with `max_tokens=1`. On success it sets
`capabilities_verified: true` **and** promotes `autonomy_mode: ASSISTED → AUTONOMOUS` in your
`config.yaml` (the template ships ASSISTED because AUTONOMOUS + unverified caps would refuse to load,
D-028):

```bash
AMCCA.exe --probe
```

**4. Seed a production** — one AUTONOMOUS production + a SCORED opportunity + a PRODUCTION budget, so the
`CONCEPT_SELECTED` gate can advance instead of blocking:

```bash
AMCCA.exe --seed-demo "Your video topic here"
```

**5. Run the orchestrator.** It loops until Ctrl+C:

```bash
AMCCA.exe --orchestrator
```

Watch the console (and `%LOCALAPPDATA%/AMCCA/logs/orchestrator-*.log`). Expect transitions up to
`SCRIPT_VERIFIED`, then a `BLOCKED` at `STORYBOARDING` with `AMCCA-MED-001`.

## Inspect the result

`%LOCALAPPDATA%/AMCCA/amcca.db` (SQLite):

```sql
SELECT from_state, to_state, occurred_at FROM state_transitions ORDER BY occurred_at;
SELECT kind, amount, currency, model_id, reconciliation_state FROM cost_events;
SELECT artifact_kind, version_no, rel_path FROM artifact_versions WHERE artifact_kind = 'SCRIPT';
SELECT text, status FROM claims WHERE production_id = '<id>';
```

The SCRIPT artifact file is under `%LOCALAPPDATA%/AMCCA/data/`.

## Provider compatibility (learned the hard way)

The gateway is a plain OpenAI-compatible HTTP client (Bearer key, `POST {base_url}/chat/completions`)
behind the SSRF guard. What that rules in and out:

- **Works:** any endpoint that accepts a raw Bearer key from any client over public HTTPS — OpenAI,
  Groq, DeepSeek, Mistral, Together, Fireworks, OpenRouter (`https://openrouter.ai/api/v1`), Google
  Gemini's OpenAI-compat endpoint (`https://generativelanguage.googleapis.com/v1beta/openai`).
- **Does NOT work — localhost routers.** OmniRoute (`localhost:20128`), a local LiteLLM, etc. The SSRF
  guard blocks loopback/private IPs at the connect callback and there is no dev override. Expose it via
  a public tunnel (`omniroute tunnel` → Cloudflare) and point `base_url` at the tunnel URL instead.
- **Does NOT work — client-gated routers.** AgentRouter (`agentrouter.org`) returns
  `unauthorized_client_error` ("unauthorized client detected") for any request that is not from an
  approved tool (opencode, Cursor, …). It checks the client, not just the key.
- **Model ids drift.** `gemini-2.0-flash` / `gemini-1.5-*` are gone for new keys. Always
  `GET {base_url}/models` (with the Bearer header, or `?key=` for Gemini native) and copy an id from
  there. A wrong id is a 404 with a helpful body; a 401 is the key/endpoint.
- **5xx is retried** (`ResilientProviderGateway`, exponential backoff). Sustained 5xx from an
  overloaded free tier still trips the circuit breaker and BLOCKs the production — restart
  `--orchestrator` (fresh circuit) or switch model.
- **The secret store is one location.** `--set-secret`, `--probe` and `--orchestrator` all use
  `%LOCALAPPDATA%\AMCCA\secrets`.

## Notes

- Research needs ≥ 2 independent authoritative sources per material claim (SPEC/26). A weak model or a
  topic with thin sourcing can loop in RESEARCHING and hit `REWORK` / the iteration cap — that is the
  gate working, not a bug. Pick a well-documented topic for the first run.
- Real Gemini calls cost money (small; the free tier usually covers a run). `contract.MaxCost`
  (2.00 per agent by default) and the production budget are the ceilings.
- To reset: delete `%LOCALAPPDATA%/AMCCA/amcca.db*` (keeps your config and secret).
