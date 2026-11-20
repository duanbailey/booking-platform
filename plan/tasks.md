# Task plan

Work one task at a time, in order, unless a task says otherwise. For each task (see `CLAUDE.md`):

1. Write tests from the listed acceptance criteria first (stories in `specs/02-requirements.md`, rules in `specs/03-business-rules.md`, endpoints in `specs/04-api.openapi.yaml`).
2. Implement until they pass.
3. Reference rule IDs in test names and code comments (e.g. `// R7`).
4. If code and spec disagree, stop and ask.

Mark a task `[x]` when its "Done when" holds. A task that touches the schema adds a migration; the schema is never edited by hand.

## Decided

- Migration tool: EF Core migrations ([ADR 0006](../docs/adr/0006-ef-core-migrations.md)), 2026-11-16.
- Staff at several businesses: allowed (`specs/05-data-model.md`), 2026-11-16.
- Local AWS for tests: in-memory fakes plus a small real-AWS suite ([ADR 0007](../docs/adr/0007-aws-testing-with-fakes.md)), 2026-11-16.
- Outbox relay hosting: background service in the API, so the API runs on ECS Fargate ([ADR 0008](../docs/adr/0008-outbox-relay-in-api.md)), 2026-11-16.
- Infrastructure as code: Terraform, S3 state backend ([ADR 0009](../docs/adr/0009-terraform-for-infrastructure.md)), 2026-11-16.
- Tokens: 15-minute HS256 access tokens, 30-day sliding refresh tokens with rotation and reuse detection, refresh and logout endpoints ([ADR 0010](../docs/adr/0010-refresh-token-rotation.md)), 2026-11-17.
- Auth details (T4): `PasswordHasher` (PBKDF2) for passwords; login gives one `InvalidCredentials` for every failure with similar timing; 10 requests per minute per IP on `/v1/auth/`, 60 for refresh and logout (429 `RequestRejected`); email and name trimmed, checked after trimming; IANA time zone names only, aliases accepted and stored under their canonical name (amended 2026-11-20 after the T13 code review: e.g. UTC is stored as Etc/UTC); null in `PATCH /v1/me` gives 400; a sign-up race is decided by the unique index, 2026-11-17.
- Password recovery and change (stories added 2026-11-17): same 202 whether or not an account exists; `password_resets` table like invites (worker makes the token, 1-hour link, single use, newest only); one recovery email per account per 5 minutes; a link also works for an account with no password yet (accepts the invite); reset and change revoke every refresh token; change returns new tokens, wrong current password is 422 `CurrentPasswordIncorrect`, endpoint under `/v1/auth/` for the rate limit; `PasswordChanged` email after both, 2026-11-17.
- T16 reading bookings (2026-11-20): on `GET /v1/businesses/{businessId}/bookings`, anyone but the owner and the business's active staff gets 404 (as availability and time off), and so does an unknown business; the owner's `staffId` must be a staff row of the business (else 404; a removed member is allowed, for their past bookings); staff see only their own staff row's bookings and another `staffId` is 403; an owner who is also staff sees everything. Defaults (existing conventions): lists ordered by start, then id, with the start/id cursor; `from` inclusive and `to` exclusive on the start; `from` after `to` is 400 naming `from`; no `status` lists both; history ordered by `at`, then id; `actor.name` is the user's current name (history stores no name); `actor.userId` (shown to the booking's staff member and the owner) and `previousSlot` (Rescheduled only) are omitted, not null, when they don't apply; a removed staff member gets 404 for their old bookings and their history (AccessResolver, T3). After code review: start/id cursors carry the kind of list (booking lists, booking history, time off), so a cursor from another kind is 400 naming `cursor` (both booking lists are one kind); a `(staff_id, start_utc)` index (migration BookingsStaffStartIndex) serves the staff and business lists, with each list's scope inside the keyset query.
- T15 confirm pattern and R4 rebook (2026-11-20): a booking is inside the window when it is Confirmed, with the same business (any staff or service), and has not started: now < start < now + N hours (exactly N hours away is outside, as cancelling then is allowed; N = 0 never triggers). The confirm pattern matches on the exact set: the change goes ahead only if `confirm.affectedBookingIds` equals the affected set recomputed under the lock, else 409 `ConfirmationRequired` with the current list; when the recomputed set is empty the change goes ahead whether or not `confirm` was sent. `affectedBookings` is ordered by start, then id. `ConfirmationRequired` is checked last, after `SlotConflict`. Booking creation locks the customer's user row FOR UPDATE (the caller's stays FOR SHARE), so one customer's bookings run one at a time across staff and the R4 check sees them all. Staff booking on behalf see every affected booking, a colleague's included (an exception to the view rule, specs/07-nfr.md). One `RebookedInsideWindow` per original booking. Defaults: the 409 message tells a customer booking for themselves to contact the business to cancel the original, and tells staff and owners that the original still stands.
- T14 bookings (2026-11-20): the 409 `SlotConflict` details are the requested slot (staff, start, end), never the other booking's times. Check order: 400, 404 (service, or a staffId outside the service's business), 403 (customerEmail or override not allowed), 422 `CustomerNotFound`, 422 `SlotUnavailable` for a removed staff member, 422 `StaffNotAssignedToService` for an active unassigned one, 422 `SlotUnavailable` when the start is in the past or not offered even ignoring the notice period and bookings (availability, step, advance window, time off), 422 `MinimumNotice` (only for a start that would otherwise be offered), then 409 `SlotConflict` (a Confirmed booking's blocked interval overlaps, R1). History actor role: Customer for a plain self-booking; Owner or Staff when booking on behalf or using the override (Owner when the caller is both); `minimumNoticeOverridden` is true only when the start really was inside the notice period (the permission check applies whenever the flag is set). An account without a password counts as an account for `customerEmail`. Before the staff lock, the customer's and actor's user rows are locked in id order (the booking and history inserts' foreign keys would otherwise take those locks after the staff lock, against ADR 0002's order); T14 took both FOR SHARE, superseded by T15 (2026-11-20): the customer's row is FOR UPDATE, the actor's stays FOR SHARE. `confirm` was validated and ignored in T14; T15 enforces it.
- T13 slots (2026-11-20): NodaTime with its embedded tzdb resolves local times, so DST rules are the same on every OS (zone-name validation moves to it too). `staffId` not a staff row of the service's business is 404; a removed or unassigned member gives 200 with no slots. A slot is offered when start ≥ now + minimum notice (inclusive; start ≥ now with `ignoreMinimumNotice`) and start ≤ now + maxAdvanceDays × 24 h; T14 reuses the check. Steps are counted on the local wall clock from each block start; local times in a spring-forward gap are dropped, repeated fall-back times are offered at their first occurrence only; a block edge inside a gap resolves to the instant the gap ends, an ambiguous block start to its earlier and an ambiguous end to its later occurrence; every slot lasts exactly the service duration in elapsed time. Overlap checks (R1) are per staff row, so a user who is staff at two businesses has independent schedules. Slots ordered by start, then staffId. Touching availability blocks are not merged: each bounds slots on its own, so no slot crosses from one into the next (R8, decided after code review). After code review: a stored zone that tzdb lacks falls back to the OS zone data (names were validated against it before T13) with a warning logged; a zone neither knows is a 500.
- T12 time off (2026-11-19): viewed like availability (owner and the business's active staff; owner also for a removed member); time off may overlap other time off and may be in the past; UtcTimestamp strictly with Z (shared check, reused by T14); the time-off list filters on overlap (ends after `from`, starts before `to`), unlike the start-based FromUtc/ToUtc of other lists. Defaults: list by start then id; writes check access, lock the staff row, re-check; timestamps kept to the microsecond; the existing (staff_id, start_utc) index serves the list (InnoDB appends id).
- T11 availability (2026-11-19): viewing raw availability is for the owner and the business's active staff (404 for others; customers see slots); the owner may view and set a removed member's availability, the member gets 404; LocalTime stays HH:mm, so availability ends at 23:59 at the latest; DELETE of a date with no exception is 404. Defaults: past dates may have exceptions; `confirm` (and `confirmAffectedBookingIds`) accepted and ignored until T19; writes lock the staff row; a malformed path date is 400 naming `date`.
- T10 staff (2026-11-19): no AccessGranted when the owner adds themselves; restoring keeps service links and availability; PUT services allowed for a removed staff member; a new staff account gets the business's time zone. Defaults: staff list by name then staffId; PUT response by service name then id; adding locks the user row then the staff row (as T6/T7); PUT locks the staff row so replacements never interleave.
- Pagination and T9 services (2026-11-19): lists order by a stated key ending in id; opaque cursor of the last sort key; bad cursor or limit outside 1-200 is 400 naming the parameter; services ordered by name (ignoring case and accents: the column collation) then id, with an index on (business_id, name, id). Service maxima: duration 1440, buffers 1440, price 99999999.99 (400 and DB checks). Name and description trimmed, blank description stored as null. Price always returned with two decimals.
- T8 settings (2026-11-19): maxima minNoticeMinutes 525600, cancellationWindowHours 8760, maxAdvanceDays 3650, slotStepMinutes 1440 (400 and DB checks); settings validated independently (a combination offering no slots is allowed); a settings change takes no staff lock.
- T7a details (2026-11-19): newest recovery link by created_at then greater id, and an unsent newer row already disables older links (as invites); no `PasswordChanged` when a link sets an account's first password; change-password leaves recovery links working; empty current password is 422 `CurrentPasswordIncorrect`, and the new password may equal the current one.
- Cross-cutting: `InternalError` (500) and `RequestRejected` (other HTTP-level 4xx such as 405, 413, 415; added 2026-11-17 after code review) added to the error codes; access tokens carry exactly the ADR 0010 claims; `X-Correlation-Id` header; built-in JSON console logging and DataAnnotations validation, no extra libraries, 2026-11-17.
- Outbox writer (T5): typed C# event records (`Events/`); `OutboxWriter.Add` only stages the row in the caller's DbContext, so the caller's SaveChanges and transaction decide whether it is written; correlation ID is the explicit argument, else the request's `X-Correlation-Id`, else a new ID; `eventId` = `outbox.id`, a version-7 GUID; `occurredAt` = `created_at`, written with milliseconds and `Z`; camelCase JSON, string enums, null fields kept; non-UTC times throw (R2). Payloads are checked against `specs/events/` in tests only, with Corvus.Json.Validator (Apache-2.0; JsonSchema.Net was passed over because of its maintenance-fee EULA), 2026-11-18.
- Admin and owner creation (T6): the admin script is a command in the API binary (`dotnet BookingPlatform.Api.dll admin grant <email> [--granted-by <email>] | revoke <email> | list`), run in production as a one-off ECS task; users named by email must have an account; bootstrap only while there are no admins; revoking the last admin is refused; granting an existing admin is a no-op. `POST /v1/admin/owners`: non-admin 403; an existing account with no password gets a new invite (inviteId set); races give 409 `AlreadyOwner`; 201 with `Location`; no staff row for the owner; a new account takes the business's time zone; business names are trimmed like person names, 2026-11-18.
- Invites (T7): token hashes (invites and recovery links) are lowercase hex SHA-256 like refresh tokens; newest invite by `created_at`, then greater `id`; an invite is valid only while `expires_at` > now and the user has no password; accepting sets `used_at` on all the user's pending invites and emits no event. Resend: admin for a user who owns a business sends role Owner with that business (wins if both apply), else an owner for active staff in their business sends role Staff; anyone else, removed staff included, gets 404 before `InviteNotPending`; no throttle. Accept, resend and create-owner lock the user row (`FOR UPDATE`), 2026-11-18.

## Decisions needed

If a new one comes up, add it here with the task it blocks, and record the answer in `specs/` or an ADR before starting that task.

- **Outbox ordering (blocks T21, raised 2026-11-18).** The relay can order rows only by `created_at`. That ties within a millisecond (version-7 GUIDs are random within a millisecond, so `id` doesn't break the tie) and comes from each API instance's clock, so two events for one message group written close together on different instances could be published out of order. No current flow writes two events for one group in a transaction, and real actions are seconds apart. Option: an auto-increment `seq` column on `outbox` (migration, `specs/05-data-model.md` change) that the relay orders by. Decide before starting T21.

## Foundation

- [x] **T1. Repository skeleton.** .NET Web API project with nullable reference types on, xUnit test project, Node TypeScript worker with Jest, and a local MySQL (e.g. Docker Compose) for integration tests. EF Core (ADR 0006) set up with an empty first migration.
  Done when: `dotnet test` and `npm test` run green on an empty test, and the integration tests can create and drop a database.
  How to run: `docker compose up -d --wait`, then `dotnet test` at the root and `npm test` in `worker/`. Integration tests use MySQL on port 3307, or the server in `BOOKING_TEST_MYSQL`.

- [x] **T2. Initial schema.** One migration creating every table in `specs/05-data-model.md` with its keys, unique constraints (e.g. `users.email`, `businesses.owner_user_id`, `staff (user_id, business_id)`, `notifications (event_id, recipient_user_id)`), foreign keys, enums and nullability (e.g. cancellation columns null while Confirmed).
  Acceptance: data model; R2 (UTC columns, IANA time zone strings).
  Done when: migration tests check the constraints that guard business rules, e.g. a duplicate email or a second business for one owner is rejected.

- [x] **T3. API cross-cutting.** Shared error shape and codes (`Error`, `ErrorCode`), validation returning `ValidationFailed`, 500 `InternalError`, JWT issuing and validation, refresh token issuing, rotation, reuse detection and revocation with the `refresh_tokens` migration (ADR 0010; endpoints in T4), correlation IDs and structured logging (`specs/07-nfr.md`), and a role resolver that works out owner, staff, customer and admin from the data per request, with the 404-versus-403 rule.
  Done when: tests cover the error shape for 400, 401, 403, 404 and 500, an expired or tampered access token gets 401, refresh rotation and reuse detection work against MySQL, and a removed staff member or a user from another business gets no access.
  Notes: probe endpoints that exist only in the test assemblies (`ProbeController`, `AccessProbeController`) exercise the pipeline until real endpoints arrive. Real endpoints use `AccessResolver` with `Access.Found` and `Access.Require` for the 404-versus-403 rule, and `[AllowAnonymous]` for operations with `security: []`.

## Accounts and businesses

- [x] **T4. Sign-up, login and profile.** `POST /v1/auth/register`, `POST /v1/auth/login`, `POST /v1/auth/refresh`, `POST /v1/auth/logout`, `GET` and `PATCH /v1/me` (including `isAdmin`, owned business and staff rows).
  Acceptance: stories "Customer can sign up", "Owner/Staff/Customer can log in". Login fails for an account with no password. ADR 0010 (refresh rotates, a reused token revokes the login, logout returns 204 for any token).
  Also: add requests for these endpoints to `src/BookingPlatform.Api/BookingPlatform.Api.http` for manual testing, with the access token from login captured in a variable and reused.

- [x] **T5. Outbox writer.** Writing an event to `outbox` in the caller's transaction, using the envelope and schemas in `specs/events/`. No relay yet.
  Done when: tests show an event is written if and only if its transaction commits, and every payload validates against its JSON Schema.
  Notes: `OutboxWriter.Add` only stages the row, so a missing SaveChanges loses the event silently. Every task that emits an event tests that its outbox row exists after the request and that its payload validates (`EventSchemas` in the test projects). Payloads are checked in tests only, so a bad payload would otherwise first fail in the worker.

- [x] **T6. Admin creates owners.** Operator script to grant and revoke admin (`platform_admins`); a grant names an existing admin as `granted_by`, except the first (bootstrap) admin. `POST /v1/admin/owners`, creating the user and invite when needed and emitting `AccessGranted`.
  Acceptance: story "Admin creates an owner and their business"; admin authorization (`specs/07-nfr.md`).
  How to run the script: `dotnet BookingPlatform.Api.dll admin grant|revoke|list ...` (with `ASPNETCORE_ENVIRONMENT=Development` locally); exit codes 0 done or nothing to change, 1 refused, 2 usage, 3 failed unexpectedly (e.g. database unreachable). Logs go to stderr at Warning in this mode, so stdout is only the script's output; each grant and revoke is logged there as an audit line with user IDs only (no emails).

- [x] **T7. Invites.** `POST /v1/auth/accept-invite` and `POST /v1/users/{userId}/resend-invite`. Tests insert the token hash directly, standing in for the worker (T26).
  Acceptance: story "Invited user sets their password" (expiry, single use, only the newest link works, `InviteNotPending`).
  Note: T6 can leave an older, still-valid invite beside a new one (an account invited as staff that is then made an owner). Test that the older link stops working once a newer invite exists. "Newest" is by `created_at`; break ties consistently (e.g. by `id`).
  Notes: `InviteService` holds both operations. Reads that must see changes committed while waiting for the user row lock use locking reads (`FOR UPDATE`): under REPEATABLE READ a plain read reuses the snapshot from the transaction's first read. `BookingDbContext.LockUserAsync` takes the lock; create-owner (T6) now uses it too. To try accept-invite by hand before T26, set `token_hash = SHA2('<token>', 256)` and `expires_at` on the newest invite (see the `.http` file).

- [x] **T7a. Password recovery and change.** `POST /v1/auth/forgot-password`, `POST /v1/auth/reset-password` and `POST /v1/auth/change-password`; the `password_resets` migration (and the new `notifications.kind` values); `PasswordResetRequested` and `PasswordChanged` events through the outbox (T5). Tests insert the token hash directly, standing in for the worker (T26).
  Acceptance: stories "User can recover a forgotten password" (same reply whether or not the account exists, 5-minute cooldown, 1-hour single-use link, only the newest works, an account without a password accepts its invite this way) and "User can change their password" (`CurrentPasswordIncorrect`); both revoke every refresh token; `ResetLinkInvalid`.

- [x] **T8. Business settings.** `GET /v1/businesses/{businessId}` and `PATCH .../settings`, with defaults from the data model.
  Acceptance: story "Owner sets up their business"; R3, R4, R8 (changes never rewrite existing bookings).

- [x] **T9. Services.** List, create, get and update services, including the optional description.
  Acceptance: story "Owner defines services"; ADR 0004 (buffer changes affect new bookings only, tested in T14).

- [x] **T10. Staff.** List and add staff (link or invite, emitting `AccessGranted`; restoring a removed row), and `PUT /v1/staff/{staffId}/services`. Removal comes in T20.
  Acceptance: stories "Owner adds staff", "Owner assigns staff to services".

## Availability and slots

- [x] **T11. Weekly availability and date exceptions.** `GET`/`PUT` weekly availability, and list, `PUT` and `DELETE` date exceptions, without the R6 check (added in T19, once bookings exist).
  Acceptance: R8 (an exception replaces the weekly availability for its date); staff story "weekly recurring availability plus date exceptions" (first three scenarios); validation (no overlapping ranges, no null row alongside ranges).

- [x] **T12. Time off.** List, create and delete time off, without the R6 check (added in T18).

- [x] **T13. Slot generation.** `GET /v1/services/{serviceId}/slots`.
  Acceptance: R8 (availability, exceptions, past, inclusive advance window, step counted from each block start, availability bounds the appointment only, blocked interval against time off); R1 (blocked intervals of Confirmed bookings, inserted directly in tests); R2 (spring-forward gap skipped, only the first hour of a fall-back overlap, cross-transition durations); R3 and `ignoreMinimumNotice`; ADR 0005 (`RangeTooLong`, past and beyond-window ranges are not errors); removed staff get no slots; story "Customer views open slots".

## Bookings

- [x] **T14. Create a booking.** `POST /v1/bookings` without the R4 rebook check: for oneself or on behalf of a customer, staff lock (ADR 0002), buffer snapshot (ADR 0004), start on an offered slot (R8), notice override (R3), history creation row, `BookingCreated`.
  Acceptance: R1, R3, R7 (a real concurrent test against MySQL: exactly one success and one 409 whose body has only staff, start and end), R8; stories "Customer books a slot", "Staff or owner books on behalf of a customer" (except the R4 scenario), "Owner or staff overrides the minimum notice period" (booking scenarios).
  Note: new bookings start at `version` 1 (`Booking.Version` defaults to 0); `BookingCreated.bookingVersion` must be at least 1 or the worker rejects the event. Test the outbox payload, not just the response.

- [x] **T15. Confirm-before-acting and R4 rebook.** A shared component for the confirm pattern (409 `ConfirmationRequired`, recomputing the affected set under the lock, re-409 on mismatch), first used on `POST /v1/bookings` for R4, emitting `RebookedInsideWindow`.
  Acceptance: R4 (rebook paragraph); the R4 scenarios in the customer and on-behalf stories; the confirm pattern in the OpenAPI description.

- [x] **T16. Reading bookings.** `GET /v1/bookings/{bookingId}`, `/v1/me/bookings`, `/v1/businesses/{businessId}/bookings` (403 for another staff member's `staffId`), and history (`actor.userId` only for staff and owners).
  Acceptance: authorization table in `specs/07-nfr.md`; ADR 0003.

- [ ] **T17. Cancel and reschedule.** `POST .../cancel` (reasons, `CancellationWindowPassed`, `BookingCancelled`, `BookingCancelled` event) and `POST .../reschedule` (one transaction under the lock, same booking ID, history, re-snapshot buffers, `BookingRescheduled`). The version column guards a cancel racing a reschedule.
  Acceptance: R4 (inclusive window), R5, R3 and R8 on the new slot; story "Customer cancels or reschedules"; override reschedule scenarios; customer same staff only.
  Locks (T15 code review, ADR 0002 "Implicit locks on user rows count"): before any staff lock, in id order, lock the actor's user row FOR SHARE and, for a reschedule, the booking customer's row FOR UPDATE. The R4 rebook check relies on every operation that can move a customer's booking into the cancellation window holding that row (`BookingScheduler.InsideCancellationWindowAsync`). Test both: a held user row makes the request wait before any staff lock, and a booking made for the customer meanwhile sees a reschedule into the window.

## Changes that cancel bookings (R6, R2)

- [ ] **T18. Time off with R6.** Overlapping bookings (by blocked interval) go through the confirm pattern; on confirmation they are cancelled with reason `TimeOff` and `BookingCancelled` events.
  Acceptance: R6; story "Staff or owner blocks time off".
  Locks: the confirming user's row FOR SHARE before the staff lock (cancelled_by references it; ADR 0002 "Implicit locks on user rows count"), with a test.

- [ ] **T19. Availability changes with R6.** Weekly availability, date exception `PUT` and `DELETE` (confirmation in the `confirmAffectedBookingIds` query parameter), reason `AvailabilityChanged`.
  Acceptance: R6, R8; the two R6 scenarios in the availability story.
  Locks: the confirming user's row FOR SHARE before the staff lock, as T18.

- [ ] **T20. Removing staff.** `POST /v1/staff/{staffId}/remove`, reason `StaffRemoved`, `removed_at` set, lock taken.
  Acceptance: story "Owner removes staff"; R6.
  Locks: the owner's row FOR SHARE before the staff lock, as T18.

- [ ] **T21. Outbox relay.** Publishes unpublished outbox rows to the SQS FIFO queue with the message group from `specs/06-events.md`, then marks them published. Hosted service in the API claiming rows with `FOR UPDATE SKIP LOCKED`, deduplication ID = `eventId` (ADR 0008); publisher interface with an in-memory fake, plus a real-AWS test (ADR 0007).
  Done when: tests show rows are published in order per message group, published at least once, and never lost between commit and publish.
  Before starting: settle "Outbox ordering" under Decisions needed.

- [ ] **T22. Business time zone change.** `POST .../time-zone-change` (all staff locks, confirm pattern, 200 no-op for the current zone, audit row, `BookingLocalTimeChanged` per affected booking) and `GET .../time-zone-changes`.
  Acceptance: R2; story "Owner changes the business's time zone".
  Locks: the owner's row FOR SHARE before all staff locks (changed_by and cancelled_by reference it; ADR 0002 "Implicit locks on user rows count"), with a test.
  Note: canonicalize the requested zone (IanaTimeZoneAttribute.Canonicalize, decided 2026-11-20) before the "current zone" no-op check, so an alias of the current zone (e.g. GB for Europe/London) is also a no-op.

## Worker

- [ ] **T23. Worker skeleton.** Lambda handler for the SQS FIFO queue: envelope and schema validation, unknown `eventType` or `schemaVersion` left to fail into the dead-letter queue, `notifications` idempotency (Pending, Sent, Failed), an email-sender interface over SES, correlation ID logging, and contact details looked up at send time.
  Done when: a duplicate event sends each recipient at most one email.

- [ ] **T24. Booking emails.** Handlers for `BookingCreated`, `BookingRescheduled` (old and new staff), `BookingCancelled`, `BookingLocalTimeChanged` and `RebookedInsideWindow`, showing both time zones (R2).
  Acceptance: worker handling table in `specs/06-events.md`; R2, R4, R6.

- [ ] **T25. Reminders.** Scheduling, moving and deleting EventBridge Scheduler reminders named by booking ID, skipping past times, and handling `ReminderDue` (dropped if stale).
  Acceptance: R9; story "Reminders go out 24 hours and 1 hour before a booking".

- [ ] **T26. Invite and account emails.** `AccessGranted` handling: token generation, hash and 7-day expiry on `user_invites`, a new token on re-send, never logging the raw token, and the AccessGranted email when there is no invite. `PasswordResetRequested` handling the same way on `password_resets` with a 1-hour expiry, and the `PasswordChanged` email. Uses a database user limited as in `specs/07-nfr.md`.
  Acceptance: worker handling table; invite stories (end to end with T7); recovery story (end to end with T7a).
  Also: skip an invite that is used or no longer the user's newest (`specs/06-events.md`), with a test for each.
  Note: `AccessGranted` with `role: Owner` and an `inviteId` (from T6: a new owner, or an existing account without a password made owner) needs owner wording in the invite email, distinct from a staff invite.

## Infrastructure and later

- [ ] **T27. Infrastructure as code.** Terraform in `infra/` (ADR 0009). API on ECS Fargate (ADR 0008), RDS MySQL, SQS FIFO queue with a FIFO dead-letter queue and an alarm when it is not empty, worker Lambda, SES, EventBridge Scheduler, Secrets Manager, CloudWatch, and the worker's limited database user.
  Done when: one command deploys a working environment, and a booking made through the API produces a confirmation email.
  Configure trusted forwarded headers (the load balancer's network) so rate limiting sees real client IPs, and make sure the API image has time zone data (tzdata) for IANA names.
  Before the first deployment: squash the EF Core migrations into one `InitialSchema` (ADR 0006). Do it once, after every task branch is merged. Copy across any hand-written `migrationBuilder.Sql` (e.g. the worker's database user, and `FixEmailCollation`, since the EF provider drops column collations), and drop and recreate any database that applied the old migrations. The schema tests must pass afterwards. Once any environment is deployed, migrations are never squashed again.
  Admin script in production: run `admin grant|revoke|list` as a one-off ECS task from the API image with the command overridden (e.g. `dotnet BookingPlatform.Api.dll admin list`) and the database secret from Secrets Manager; document it in the runbook. The first admin needs an existing account, so the operator signs up through the API before the bootstrap grant. Grant and revoke audit lines go to CloudWatch with the task's logs (stderr).

- [ ] **T28. Slot query performance.** A repeatable test that a 30-day slot query meets p95 under 300 ms (`specs/07-nfr.md`) on realistic data; fix any shortfall.

- [ ] **T29. Idempotency-Key on `POST /v1/bookings`.** Add the header to `specs/04-api.openapi.yaml` and implement it: a retried request with the same key returns the original result and creates no second booking.

- [ ] **T30. Clean JSON type errors in `ValidationFailed`.** When a field has the wrong JSON type (e.g. `{"email":5}` on login, `{"slotStepMinutes":"30"}` on business settings), the 400 lists the field correctly but also a spurious `"body": "The request field is required."`, because MVC adds an error for the unbound request parameter after the JSON error. Report only the offending field (named as in the request, camelCase), for every endpoint with a body.
  Also: `[Required]` messages name the C# property (`"The Price field is required."`, `"The Name field is required."` on sign-up); use one plain message, `"Is required."`, everywhere (as range errors already say `"Must be between {1} and {2}."`).
  Acceptance: a wrong-typed field gives exactly one entry in `details.fields`, under its own name; a missing required field says `"Is required."`; malformed JSON (not parseable at all) still gives a single body-level error; existing validation tests keep passing.
