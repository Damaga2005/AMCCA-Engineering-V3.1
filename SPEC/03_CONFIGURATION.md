# 03 — Configuration

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Contract

Configuration is validated against `SCHEMAS/config.schema.json` at startup. There is exactly one
configuration vocabulary; `CONFIG/config.example.yaml` and every environment file use it.

> *V2 defect closed:* `budgets.yaml` and `config.example.yaml` used different key names
> (`production_eur` vs `per_production_eur`, `rework_eur` vs `max_rework_eur`) for the same
> concepts, and no schema existed to catch it.

## Layering

`defaults -> environment file -> user configuration -> environment variables`, later wins.
The merged result is validated as a whole. A partial file that is valid in isolation but invalid when
merged is a startup failure, because the merged document is the one that governs behaviour.

## Consistency rules enforced by preflight

These are checked after schema validation, because they are cross-field and a schema cannot express them:

1. `budgets.daily * 28 >= budgets.monthly` is **not** required, but `budgets.daily <= budgets.monthly` **is**.
   A daily cap above the monthly cap is rejected with `AMCCA-CFG-004`.
2. `warn_percent < pause_percent < block_percent <= 100`.
3. `budgets.per_production <= budgets.daily`.
4. `autonomy_mode = AUTONOMOUS` requires `providers.gateway.capabilities_verified = true`.
5. `publishing_enabled = true` with `environment = DEVELOPMENT` is rejected.
6. Every `*_secret_ref` matches `^secret://`; a literal value is rejected with `AMCCA-SEC-002`.
7. `data_root` is writable and has at least `storage.minimum_free_gb` available.

Rule 1 exists because V2 shipped a daily cap of 20 against a monthly cap of 300, which is internally
inconsistent for any month with more than fifteen active days, and nothing checked it.

## Secrets in configuration

Configuration holds references, never values. The reference format is `secret://<vault>/<name>`.
Resolution happens through `ISecretStore` at point of use, and the resolved value is never placed in a
structure that a logger, an exporter or a crash dump can reach.

## Changing configuration at runtime

Autonomy mode, publishing enablement, budgets and policy activation are changed through audited operator
commands, not by editing a file. File changes require a restart and a fresh preflight, because a
half-applied configuration is a state the system has no way to reason about.

## Deployed configuration file location

`config.yaml` lives at `%LocalAppData%\AMCCA\config.yaml`, next to `amcca.db` (same directory as
`DataRoot`). If the operator has not placed a file there, startup falls back to `AmccaConfig`'s built-in
safe defaults (`DryRun = true`, publishing disabled) scoped to that same directory, rather than failing —
absence of an optional file is not a configuration error. This mirrors the existing `amcca.db` convention
instead of introducing a second, unrelated install-layout rule; there is no reason for the two files that
constitute one install's state to live in different places.
