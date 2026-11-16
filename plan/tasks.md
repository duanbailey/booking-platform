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

## Decisions needed

None open as of 2026-11-16. If a new one comes up, add it here with the task it blocks, and record the answer in `specs/` or an ADR before starting that task.

## Foundation

- [x] **T1. Repository skeleton.** .NET Web API project with nullable reference types on, xUnit test project, Node TypeScript worker with Jest, and a local MySQL (e.g. Docker Compose) for integration tests. EF Core (ADR 0006) set up with an empty first migration.
  Done when: `dotnet test` and `npm test` run green on an empty test, and the integration tests can create and drop a database.
  How to run: `docker compose up -d --wait`, then `dotnet test` at the root and `npm test` in `worker/`. Integration tests use MySQL on port 3307, or the server in `BOOKING_TEST_MYSQL`.

- [x] **T2. Initial schema.** One migration creating every table in `specs/05-data-model.md` with its keys, unique constraints (e.g. `users.email`, `businesses.owner_user_id`, `staff (user_id, business_id)`, `notifications (event_id, recipient_user_id)`), foreign keys, enums and nullability (e.g. cancellation columns null while Confirmed).
  Acceptance: data model; R2 (UTC columns, IANA time zone strings).
  Done when: migration tests check the constraints that guard business rules, e.g. a duplicate email or a second business for one owner is rejected.

- [ ] **T3. API cross-cutting.** Shared error shape and codes (`Error`, `ErrorCode`), validation returning `ValidationFailed`, JWT issuing and validation, correlation IDs and structured logging (`specs/07-nfr.md`), and a role resolver that works out owner, staff, customer and admin from the data per request, with the 404-versus-403 rule.
  Done when: tests cover the error shape for 400, 401, 403 and 404, and that a removed staff member or a user from another business gets no access.

## Accounts and businesses

- [ ] **T4. Sign-up, login and profile.** `POST /v1/auth/register`, `POST /v1/auth/login`, `GET` and `PATCH /v1/me` (including `isAdmin`, owned business and staff rows).
  Acceptance: stories "Customer can sign up", "Owner/Staff/Customer can log in". Login fails for an account with no password.

- [ ] **T5. Outbox writer.** Writing an event to `outbox` in the caller's transaction, using the envelope and schemas in `specs/events/`. No relay yet.
  Done when: tests show an event is written if and only if its transaction commits, and every payload validates against its JSON Schema.

- [ ] **T6. Admin creates owners.** Operator script to grant and revoke admin (`platform_admins`); a grant names an existing admin as `granted_by`, except the first (bootstrap) admin. `POST /v1/admin/owners`, creating the user and invite when needed and emitting `AccessGranted`.
  Acceptance: story "Admin creates an owner and their business"; admin authorization (`specs/07-nfr.md`).

- [ ] **T7. Invites.** `POST /v1/auth/accept-invite` and `POST /v1/users/{userId}/resend-invite`. Tests insert the token hash directly, standing in for the worker (T26).
  Acceptance: story "Invited user sets their password" (expiry, single use, only the newest link works, `InviteNotPending`).

- [ ] **T8. Business settings.** `GET /v1/businesses/{businessId}` and `PATCH .../settings`, with defaults from the data model.
  Acceptance: story "Owner sets up their business"; R3, R4, R8 (changes never rewrite existing bookings).

- [ ] **T9. Services.** List, create, get and update services, including the optional description.
  Acceptance: story "Owner defines services"; ADR 0004 (buffer changes affect new bookings only, tested in T14).

- [ ] **T10. Staff.** List and add staff (link or invite, emitting `AccessGranted`; restoring a removed row), and `PUT /v1/staff/{staffId}/services`. Removal comes in T20.
  Acceptance: stories "Owner adds staff", "Owner assigns staff to services".

## Availability and slots

- [ ] **T11. Weekly availability and date exceptions.** `GET`/`PUT` weekly availability, and list, `PUT` and `DELETE` date exceptions, without the R6 check (added in T19, once bookings exist).
  Acceptance: R8 (an exception replaces the weekly availability for its date); staff story "weekly recurring availability plus date exceptions" (first three scenarios); validation (no overlapping ranges, no null row alongside ranges).

- [ ] **T12. Time off.** List, create and delete time off, without the R6 check (added in T18).

- [ ] **T13. Slot generation.** `GET /v1/services/{serviceId}/slots`.
  Acceptance: R8 (availability, exceptions, past, inclusive advance window, step counted from each block start, availability bounds the appointment only, blocked interval against time off); R1 (blocked intervals of Confirmed bookings, inserted directly in tests); R2 (spring-forward gap skipped, only the first hour of a fall-back overlap, cross-transition durations); R3 and `ignoreMinimumNotice`; ADR 0005 (`RangeTooLong`, past and beyond-window ranges are not errors); removed staff get no slots; story "Customer views open slots".

## Bookings

- [ ] **T14. Create a booking.** `POST /v1/bookings` without the R4 rebook check: for oneself or on behalf of a customer, staff lock (ADR 0002), buffer snapshot (ADR 0004), start on an offered slot (R8), notice override (R3), history creation row, `BookingCreated`.
  Acceptance: R1, R3, R7 (a real concurrent test against MySQL: exactly one success and one 409 whose body has only staff, start and end), R8; stories "Customer books a slot", "Staff or owner books on behalf of a customer" (except the R4 scenario), "Owner or staff overrides the minimum notice period" (booking scenarios).

- [ ] **T15. Confirm-before-acting and R4 rebook.** A shared component for the confirm pattern (409 `ConfirmationRequired`, recomputing the affected set under the lock, re-409 on mismatch), first used on `POST /v1/bookings` for R4, emitting `RebookedInsideWindow`.
  Acceptance: R4 (rebook paragraph); the R4 scenarios in the customer and on-behalf stories; the confirm pattern in the OpenAPI description.

- [ ] **T16. Reading bookings.** `GET /v1/bookings/{bookingId}`, `/v1/me/bookings`, `/v1/businesses/{businessId}/bookings` (403 for another staff member's `staffId`), and history (`actor.userId` only for staff and owners).
  Acceptance: authorization table in `specs/07-nfr.md`; ADR 0003.

- [ ] **T17. Cancel and reschedule.** `POST .../cancel` (reasons, `CancellationWindowPassed`, `BookingCancelled`, `BookingCancelled` event) and `POST .../reschedule` (one transaction under the lock, same booking ID, history, re-snapshot buffers, `BookingRescheduled`). The version column guards a cancel racing a reschedule.
  Acceptance: R4 (inclusive window), R5, R3 and R8 on the new slot; story "Customer cancels or reschedules"; override reschedule scenarios; customer same staff only.

## Changes that cancel bookings (R6, R2)

- [ ] **T18. Time off with R6.** Overlapping bookings (by blocked interval) go through the confirm pattern; on confirmation they are cancelled with reason `TimeOff` and `BookingCancelled` events.
  Acceptance: R6; story "Staff or owner blocks time off".

- [ ] **T19. Availability changes with R6.** Weekly availability, date exception `PUT` and `DELETE` (confirmation in the `confirmAffectedBookingIds` query parameter), reason `AvailabilityChanged`.
  Acceptance: R6, R8; the two R6 scenarios in the availability story.

- [ ] **T20. Removing staff.** `POST /v1/staff/{staffId}/remove`, reason `StaffRemoved`, `removed_at` set, lock taken.
  Acceptance: story "Owner removes staff"; R6.

- [ ] **T21. Outbox relay.** Publishes unpublished outbox rows to the SQS FIFO queue with the message group from `specs/06-events.md`, then marks them published. Hosted service in the API claiming rows with `FOR UPDATE SKIP LOCKED`, deduplication ID = `eventId` (ADR 0008); publisher interface with an in-memory fake, plus a real-AWS test (ADR 0007).
  Done when: tests show rows are published in order per message group, published at least once, and never lost between commit and publish.

- [ ] **T22. Business time zone change.** `POST .../time-zone-change` (all staff locks, confirm pattern, 200 no-op for the current zone, audit row, `BookingLocalTimeChanged` per affected booking) and `GET .../time-zone-changes`.
  Acceptance: R2; story "Owner changes the business's time zone".

## Worker

- [ ] **T23. Worker skeleton.** Lambda handler for the SQS FIFO queue: envelope and schema validation, unknown `eventType` or `schemaVersion` left to fail into the dead-letter queue, `notifications` idempotency (Pending, Sent, Failed), an email-sender interface over SES, correlation ID logging, and contact details looked up at send time.
  Done when: a duplicate event sends each recipient at most one email.

- [ ] **T24. Booking emails.** Handlers for `BookingCreated`, `BookingRescheduled` (old and new staff), `BookingCancelled`, `BookingLocalTimeChanged` and `RebookedInsideWindow`, showing both time zones (R2).
  Acceptance: worker handling table in `specs/06-events.md`; R2, R4, R6.

- [ ] **T25. Reminders.** Scheduling, moving and deleting EventBridge Scheduler reminders named by booking ID, skipping past times, and handling `ReminderDue` (dropped if stale).
  Acceptance: R9; story "Reminders go out 24 hours and 1 hour before a booking".

- [ ] **T26. Invite emails.** `AccessGranted` handling: token generation, hash and 7-day expiry on `user_invites`, a new token on re-send, never logging the raw token, and the AccessGranted email when there is no invite. Uses a database user limited as in `specs/07-nfr.md`.
  Acceptance: worker handling table; invite stories (end to end with T7).

## Infrastructure and later

- [ ] **T27. Infrastructure as code.** Terraform in `infra/` (ADR 0009). API on ECS Fargate (ADR 0008), RDS MySQL, SQS FIFO queue with a FIFO dead-letter queue and an alarm when it is not empty, worker Lambda, SES, EventBridge Scheduler, Secrets Manager, CloudWatch, and the worker's limited database user.
  Done when: one command deploys a working environment, and a booking made through the API produces a confirmation email.

- [ ] **T28. Slot query performance.** A repeatable test that a 30-day slot query meets p95 under 300 ms (`specs/07-nfr.md`) on realistic data; fix any shortfall.

- [ ] **T29. Idempotency-Key on `POST /v1/bookings`.** Add the header to `specs/04-api.openapi.yaml` and implement it: a retried request with the same key returns the original result and creates no second booking.
