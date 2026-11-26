# ADR 0012: Provide a web frontend for end users

- **Status:** Accepted
- **Date:** 2026-11-26
- **Related rules:** R2, R4, R6, R7 ([specs/03-business-rules.md](../../specs/03-business-rules.md))

## Context

The API is complete (T1-T31), but end users have no way to reach it: owners, staff and customers would have to call it directly. The stories in [specs/02-requirements.md](../../specs/02-requirements.md) already assume pages ("on the login page", "on the owner creation screen"), and the worker's emails link to pages that don't exist yet (`/accept-invite`, `/reset-password`, `/forgot-password`). Several rules also need the user interface to do part of the work: showing both time zones and elapsed duration (R2), warning that an original booking still stands (R4), asking for confirmation before bookings are cancelled (R6), and explaining a conflict (R7).

## Options considered

1. **A single-page app served as static files** (chosen). The browser calls the existing API. Static files need no servers and scale with a CDN (S3 + CloudFront). It suits the interactive parts: browsing slots, the confirm-and-retry flow, and showing times in two zones.
2. **Server-rendered pages from the API** (e.g. Razor Pages). No separate build or hosting, but it ties the UI to the C# deployment, and the interactive parts would need client-side scripting anyway.
3. **A native mobile app.** Already a non-goal ([specs/01-overview.md](../../specs/01-overview.md)).

A command-line interface was not considered for end users; the admin's CLI (T6) stays as it is.

## Decision

Provide a web single-page app that covers every end-user story in specs/02-requirements.md, for customers, staff, owners and admins (admins create owners and resend owners' invites; decided 2026-11-26). It is written in TypeScript with React and React Router, organised by feature-sliced design, and tested with Vitest and Testing Library. It lives in `web/`. Screens and behaviour are specified in [specs/09-ui.md](../../specs/09-ui.md).

## Consequences

- Hosting (decided 2026-11-26): the app's static files in S3, served by the existing CloudFront distribution, which sends `/v1/*` to the API and everything else to the app (an unknown path gets `index.html`, so React Router handles it). One origin, so the API needs no CORS. Locally, the Vite dev server proxies `/v1` to the API.
- Tokens (decided 2026-11-26): the refresh token moves to an HttpOnly cookie and the access token is kept in memory ([ADR 0013](0013-refresh-token-in-httponly-cookie.md), amending ADR 0010).
- The worker's `APP_BASE_URL` is the CloudFront address, and the app must serve the routes its emails link to.
- A new CI job builds and tests `web/`.
- Browser support and accessibility (decided 2026-11-26): the last two versions of Chrome, Edge, Firefox and Safari (desktop and iOS), and WCAG 2.2 level AA, in specs/07-nfr.md. specs/08-architecture.md gains the frontend now, docs/architecture.md once it is deployed.
