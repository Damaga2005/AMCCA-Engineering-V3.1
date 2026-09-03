# 43 — OAuth and Credentials

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## Flow

1. Generate a cryptographically random `state` and a PKCE verifier and challenge.
2. Open the platform's official authorisation URL in WebView2 or the system browser.
3. Receive the callback on a loopback redirect. **Validate the exact `state` and the exact redirect URI.**
   An unsolicited or mismatched `state` is rejected and audited.
4. Exchange the code using the PKCE verifier.
5. Store the tokens in `SecretStore`. Persist only a `secret://` reference and non-sensitive metadata.
6. Probe account identity and capabilities; write `platform_capabilities`.
7. Audit the connection with the granted scopes.

PKCE is used even where the platform would accept a confidential-client flow, because a desktop
application cannot keep a client secret and pretending otherwise is a false sense of security.

## Scopes

Least privilege. Request only the scopes the configured capabilities require. A scope grant that exceeds
what is needed is recorded and surfaced to the operator, because an over-scoped token is a liability the
operator should be able to see.

## Refresh

Uses the provider's documented mechanism. A refresh failure moves the account to `REAUTH_REQUIRED`,
blocks autonomous publication for that account, and raises a notification. It does not retry indefinitely
and does not fall back to a stale token.

## Token hygiene

No token is printed, logged, exported, included in a diagnostics bundle or written to the database.
The redaction middleware and a dedicated security test enforce this (`SPEC/72`). A token that appears in
any artifact is a release blocker.

## Revocation

Disconnecting an account revokes the token where the platform supports revocation, deletes it from the
secret store, and marks the account `DISCONNECTED`. Publications already made are unaffected; their
evidence remains.
