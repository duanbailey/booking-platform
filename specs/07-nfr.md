- Slot query p95 under 300 ms. A slot query covers at most 30 days, so this is the worst case; longer ranges are rejected with HTTP 400 (see [ADR 0005](../docs/adr/0005-limit-slot-query-range.md)).
- Auth: JWT carrying the user's ID, signed with HS256 and valid for 15 minutes, renewed with a single-use refresh token valid for 30 days from issue; using a refresh token twice revokes that login (see [ADR 0010](../docs/adr/0010-refresh-token-rotation.md)). Roles are not claims in the token; they come from the data (see Authorization below).
- Database access for auth data: the notification worker's database user may update only `token_hash` and `expires_at` on `user_invites` (see [specs/06-events.md](06-events.md)). It cannot insert or delete invites, or read or write `users.password_hash`.
- Structured logging with correlation IDs across the C# and Node services. Logs are JSON lines on stdout (CloudWatch collects them). Every API response has an `X-Correlation-Id` header: the client's value if it sends one of 1-64 characters from `A-Z a-z 0-9 - _ .`, otherwise a new ID. It is attached to every log line for the request and becomes the `correlationId` of events the request causes (specs/06-events.md). Logs never contain passwords, tokens or token hashes.
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
| Set weekly availability and date exceptions (R8) | No | Their own | Any staff in the business |
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
