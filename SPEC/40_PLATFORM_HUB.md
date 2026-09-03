# 40 — Platform Hub

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Role

The Platform Hub owns platform accounts, their credentials, their verified capabilities and the
dispatch of publication intents. It is the only component that talks to `IPlatformAdapter`.

## Account lifecycle

`DISCONNECTED -> CONNECTED -> REAUTH_REQUIRED -> CONNECTED`, plus `SUSPENDED` and `DISABLED`.
An account carries a `secret://` credential reference, never a token. The table enforces this:
`CHECK(credential_secret_ref LIKE 'secret://%')`.

## Capability gating

Nothing is dispatched to a platform without a `platform_capabilities` row that is `VERIFIED` and
unexpired for the specific capability being used, on the specific account being used (`SPEC/42`).
An expired verification degrades to `UNVERIFIED` and blocks autonomous publication.

Capability is per account, not per platform. Two accounts on the same platform can have materially
different permissions — different monetisation status, different content-length limits, different API
access — and assuming otherwise produces failures that look random.

## Publication lock

One publication lock per `(production_id, platform, account_id)`. It prevents concurrent dispatch of the
same content to the same target. It is a complement to, not a replacement for, the unique constraint on
`publications` — the lock avoids the collision, the constraint guarantees the outcome if the lock fails.

## Dry run

When `dry_run = true`, every `EXTERNAL_UNSAFE` adapter call is blocked at the tool registry. The hub still
builds intents, runs preflight and records what it *would* have sent, so a dry run exercises the whole
path except the effect. It never fabricates a success response.
