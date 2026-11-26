# ADR 0013: Keep the refresh token in an HttpOnly cookie instead of the response body

- **Status:** Accepted (amends [ADR 0010](0010-refresh-token-rotation.md))
- **Date:** 2026-11-26
- **Related:** [ADR 0012](0012-provide-web-frontend.md) (web frontend), [specs/07-nfr.md](../../specs/07-nfr.md) (Auth)

## Context

ADR 0010 returns the access token and the refresh token in the response body, and the client sends the refresh token back in the body of `POST /v1/auth/refresh` and `POST /v1/auth/logout`. The web app (ADR 0012) must keep them somewhere. A refresh token is valid for 30 days and renews itself, so whoever steals one has a long-lived login (until reuse detection catches it, if the real user refreshes again). The app is served from the same origin as the API (ADR 0012).

## Options considered

1. **localStorage.** No API change and reloads stay logged in, but any script running on the page can read it, so one XSS bug leaks 30-day logins.
2. **Memory only.** No API change and nothing to steal later, but every reload or new tab logs the user out.
3. **The refresh token in an HttpOnly cookie, the access token in memory** (chosen). Page scripts can't read the cookie, and a reload gets a new access token from `POST /v1/auth/refresh`. An XSS bug can still act as the user while the page is open, but can't take a login away with it.

## Decision

- Every response that starts or renews a login (register, login, accept-invite, reset-password, change-password, refresh) sets the refresh token in a cookie `refresh_token` with `HttpOnly; Secure; SameSite=Strict; Path=/v1/auth`, and `Max-Age` running to the token's expiry (30 days, sliding as in ADR 0010).
- Those responses no longer contain `refreshToken`. They keep `refreshTokenExpiresAt`, and the access token stays in the body; the app keeps it in memory only.
- `POST /v1/auth/refresh` and `POST /v1/auth/logout` read the token from the cookie and take no body. Logout also clears the cookie. With no cookie (or an empty one), refresh is 401 `InvalidRefreshToken` and logout is 204, as for an unknown token today. A refresh that fails with 401 also clears the cookie, so the browser stops sending a dead token (decided 2026-11-26, T32). Both clear it only when the request carried one (T32 review, 2026-11-26): see the next point.
- Cross-site requests: `SameSite=Strict` means the browser sends the cookie only on requests from the app's own origin, and a cross-site page couldn't read the response of a forged refresh anyway. Refresh and logout take no body, so a page elsewhere can submit a plain form to them; it arrives without the cookie, and browsers still apply a `Set-Cookie` on the response to a top-level form post. So the API never clears the cookie on a request that didn't carry it, otherwise any site could log the user out (found in the T32 review, 2026-11-26). Login, which does set the cookie, needs a JSON body, which a cross-site page can't send without a CORS preflight the API doesn't allow. No CSRF token is added.
- Every response that sets or clears the cookie sends `Cache-Control: no-store` (decided 2026-11-26, T32).
- Rotation, reuse detection, families and expiry are unchanged (ADR 0010). Two tabs refreshing at once with the same cookie would look like reuse and revoke the login; the app prevents it by running refreshes one at a time across tabs (T34), not by loosening reuse detection (decided 2026-11-26).

## Consequences

- An API change, made in T32: the auth operations and the `AuthToken` schema in specs/04-api.openapi.yaml, the auth endpoints, their tests, and the requests in `BookingPlatform.Api.http`.
- Clients other than a browser must keep cookies. There are none apart from the tests and the `.http` file.
- `Secure` cookies need HTTPS, which CloudFront provides; browsers also accept them on `http://localhost`, so local development works through the Vite proxy.
- The app is logged out when the cookie is gone (cleared by the user, or 30 days without a visit), as before.
