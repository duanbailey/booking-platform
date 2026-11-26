- Slot query p95 under 300 ms. A slot query covers at most 30 days, so this is the worst case; longer ranges are rejected with HTTP 400 (see [ADR 0005](../docs/adr/0005-limit-slot-query-range.md)).
- Auth: JWT carrying the user's ID, signed with HS256 and valid for 15 minutes, renewed with a single-use refresh token valid for 30 days from issue, kept in an HttpOnly cookie ([ADR 0013](../docs/adr/0013-refresh-token-in-httponly-cookie.md); the API change is made in the first frontend task); using a refresh token twice revokes that login (see [ADR 0010](../docs/adr/0010-refresh-token-rotation.md)). Roles are not claims in the token; they come from the data (see Authorization below).
- Passwords: hashed with ASP.NET Core's `PasswordHasher` (PBKDF2-HMAC-SHA512, 100,000 iterations, random salt; decided 2026-11-17). Its versioned format lets a hash be upgraded at the next successful login. Passwords are never trimmed or logged; the only rule is 8-128 characters.
- Login never reveals whether an account exists or has a password: an unknown email, a wrong password and an account without a password all get the same 401 `InvalidCredentials`, and a dummy hash check keeps their timing alike. Sign-up does reveal an existing email (409 `EmailTaken`), as the story requires; rate limiting bounds how fast anyone can probe.
- Rate limiting, per client IP in fixed one-minute windows, then 429 `RequestRejected` with `Retry-After`: 10 requests a minute shared by the `/v1/auth/` endpoints that check a password or send email (register, login, accept-invite, forgot-password, reset-password, change-password), and a separate 60 a minute for refresh and logout, so many users behind one IP refreshing every 15 minutes don't block each other's logins. Each API instance counts on its own, so with several instances the effective limit is higher. Behind the load balancer the client IP comes from `X-Forwarded-For`, trusted only from the load balancer's network (T27).
- Database access for auth data: the notification worker's database user may update only `token_hash` and `expires_at` on `user_invites` and `password_resets` (see [specs/06-events.md](06-events.md)). It cannot insert or delete invites or recovery links, or read or write `users.password_hash`.
- Database access for the API: the API connects as a user that may only read and write data (SELECT, INSERT, UPDATE, DELETE), never change the schema. Migrations, and creating or updating the API's and the worker's database users, run separately as the master user (`db migrate`, decided 2026-11-24).
- Password recovery never reveals whether an account exists: `POST /v1/auth/forgot-password` returns the same 202 whether or not an email is sent, including during the 5-minute per-account cooldown. Setting a password through a recovery link, and changing it while logged in, revoke every refresh token for the user.
- Structured logging with correlation IDs across the C# and Node services. Logs are JSON lines on stdout (CloudWatch collects them). Every API response has an `X-Correlation-Id` header: the client's value if it sends one of 1-64 characters from `A-Z a-z 0-9 - _ .`, otherwise a new ID. It is attached to every log line for the request and becomes the `correlationId` of events the request causes (specs/06-events.md). Logs never contain passwords, tokens or token hashes.
- Web app browsers (ADR 0012, decided 2026-11-26): the last two versions of Chrome, Edge, Firefox and Safari, on desktop and iOS.
- Web app accessibility (ADR 0012, decided 2026-11-26): WCAG 2.2 level AA. Checked by automated accessibility tests (e.g. axe) in the component tests, plus a manual keyboard pass for each screen.
- Errors: every error response uses the shared `Error` shape in specs/04-api.openapi.yaml. A request refused at the HTTP level (e.g. 405, 413, 415) keeps its status with code `RequestRejected`. An unexpected failure returns 500 `InternalError` with a generic message; the exception is logged with the correlation ID, never returned.

# Authorization

A user's roles in a business come from links in the data ([specs/05-data-model.md](05-data-model.md)), not from a user type:

- **Owner** of a business: the user is the business's `owner_user_id`.
- **Staff** in a business: the user has a `staff` row in that business. An owner who takes bookings is also staff.
- **Customer** of a booking: the user is the booking's `customer_id`.
- **Admin** of the platform: the user has a `platform_admins` row. This is the only role not scoped to a business.

The API checks these links on every request, so a change (e.g. a staff member removed from a business) takes effect immediately, without waiting for tokens to expire. Every check is scoped to one business: being owner or staff in one business grants nothing in another.

## Who can do what

| Action | Customer | Staff | Owner |
|---|---|---|---|
| View services and open slots | Yes, for any business | Yes | Yes |
| Change business settings, including time zone (R2, R3, R4, R8) | No | No | Yes |
| Create and edit services | No | No | Yes |
| Add and remove staff (removal under R6); assign staff to services | No | No | Yes |
| Resend a staff member's invite | No | No | Yes |
| View weekly availability and date exceptions (customers see open slots instead) | No | Any staff in their business | Any staff in the business |
| Set weekly availability and date exceptions (R8) | No | Their own | Any staff in the business |
| View time off | No | Any staff in their business | Any staff in the business |
| Block time off (R6) | No | Their own | Any staff in the business |
| Create a booking | For themselves | For a customer, with themselves as the staff member | For a customer, with any staff in the business |
| View a booking | Their own | Bookings where they are the staff member | Any booking in the business |
| Cancel a booking | Their own, subject to R4 | Bookings where they are the staff member | Any booking in the business |
| Reschedule a booking | Their own, subject to R4, with the same staff member | Bookings where they are the staff member, to a slot of their own | Any booking in the business, to any staff in the business |
| Override the minimum notice period (R3) | No | Bookings where they are the staff member, both before and after a reschedule | Any booking in the business |

"Their own" for staff means the staff row is theirs. R4 refusals for customers are business-rule errors, not authorization errors.

An admin can create owner accounts and resend an owner's invite (see `specs/02-requirements.md`), and nothing else. Being an admin grants no access inside any business: an admin sees a business's bookings, availability, staff and settings only if they are also its owner or staff. The admin role cannot be granted or revoked through the API.

## Responses

- **401** when the request has no valid token.
- **404** when the user cannot view the resource at all (e.g. a customer asking for someone else's booking), so the API never reveals that it exists.
- **403** when the user can view the resource but cannot perform the action (e.g. a staff member changing business settings).
- A 409 conflict (R7) never includes another booking's ID or customer details, whoever the caller is.
- Exception (decided 2026-11-20): when staff book on behalf of a customer who holds bookings inside the cancellation window (R4), the 409 `ConfirmationRequired` lists all of them (ID, staff member, start, end), including bookings with other staff, so the staff member can be warned and confirm. It gives no other access: viewing such a booking is still 404.
