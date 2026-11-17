# ADR 0010: Short-lived access tokens with rotating refresh tokens

- **Status:** Accepted
- **Date:** 2026-11-17
- **Related specs:** [specs/07-nfr.md](../../specs/07-nfr.md), [specs/04-api.openapi.yaml](../../specs/04-api.openapi.yaml), [specs/05-data-model.md](../../specs/05-data-model.md)

## Context

The API authenticates with a JWT carrying the user's ID (specs/07-nfr.md). Roles are checked against the data on every request, so a removed staff member loses access at once whatever the token says. The token still proves who the caller is, though: a leaked token lets someone act as that user until it expires. With no way to renew a token, its lifetime had to trade exposure (long tokens) against how often users log in again (short tokens).

## Options considered

1. **One long-lived access token (e.g. 12 hours), no refresh.** Simplest. A leaked token works for the whole lifetime and cannot be revoked short of rotating the signing key for everyone.
2. **Short access token plus a refresh token that never changes.** Users stay logged in, but a leaked refresh token works for its whole lifetime, and nothing shows that it leaked.
3. **Short access token plus a rotating refresh token with reuse detection** (chosen). Each refresh returns a new refresh token and retires the old one. A retired token presented again means two parties hold the same token, so the whole login is revoked.

## Decision

- **Access token:** a JWT signed with HS256 (only the API issues and checks tokens, so a key pair adds nothing), valid for 15 minutes. Claims: `sub` (user ID), `iat`, `exp`, `iss`, `aud`; no role claims. The signing key comes from configuration (Secrets Manager in AWS).
- **Refresh token:** a random 256-bit value, valid for 30 days. Only its SHA-256 hash is stored, in `refresh_tokens` (specs/05-data-model.md), like invite tokens.
- **Login session (family):** register, login and accept-invite each start a family. Every refresh token issued from it shares the family ID.
- **Rotation:** `POST /v1/auth/refresh` marks the presented token used and returns a new access token and a new refresh token in the same family. Expiry is sliding: the new refresh token is valid for 30 days from the refresh, so a user who returns at least every 30 days stays logged in.
- **Reuse detection:** presenting a token that was already used revokes every token in its family, and the call fails with 401 `InvalidRefreshToken`. The user logs in again.
- **Logout:** `POST /v1/auth/logout` revokes the presented token's family. It needs no access token, so a client whose access token has expired can still log out, and it returns 204 even for an unknown token, so it reveals nothing.
- An unknown, expired, used or revoked refresh token gets the same 401 `InvalidRefreshToken`, without saying which.

## Consequences

- Clients refresh every 15 minutes or on a 401 `Unauthenticated`, and must store the newest refresh token each time.
- Two refreshes racing with the same token (e.g. two browser tabs) look like reuse: the second revokes the family and that user logs in again. This is accepted; a client should make refresh calls one at a time.
- A stolen access token works for at most 15 minutes. Revoking a family does not invalidate access tokens already issued from it.
- `refresh_tokens` grows with each refresh. Expired rows can be deleted by a cleanup job later; nothing reads them after expiry.
- There is no "log out everywhere" endpoint. It can be added later by revoking every family for a user.
