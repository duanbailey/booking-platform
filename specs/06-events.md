# Events

The API publishes domain events to SQS. The Node worker consumes them, sends email via SES, schedules reminders with EventBridge Scheduler, and records delivery status in `notifications`. Each event is defined as a JSON Schema in [events/](events/).

## Event list

| Event | Producer | When | Schema |
|---|---|---|---|
| BookingCreated | API | A booking is made. | [BookingCreated.v1](events/BookingCreated.v1.schema.json) |
| BookingRescheduled | API | A booking is moved (R5). Never accompanied by BookingCancelled or BookingCreated ([ADR 0003](../docs/adr/0003-reschedule-keeps-booking-id.md)). | [BookingRescheduled.v1](events/BookingRescheduled.v1.schema.json) |
| BookingCancelled | API | A booking is cancelled, for any reason, including R2 and R6. One event per booking. | [BookingCancelled.v1](events/BookingCancelled.v1.schema.json) |
| BookingLocalTimeChanged | API | A business time zone change leaves a booking that has not started at a different local time in the business's time zone (R2). One event per booking. | [BookingLocalTimeChanged.v1](events/BookingLocalTimeChanged.v1.schema.json) |
| RebookedInsideWindow | API | A booking is made for a customer (by them, or by staff or an owner on their behalf) while the customer holds a booking with the same business inside the cancellation window (R4). Sent in addition to BookingCreated. | [RebookedInsideWindow.v1](events/RebookedInsideWindow.v1.schema.json) |
| ReminderDue | EventBridge Scheduler | A reminder's time arrives (R9). | [ReminderDue.v1](events/ReminderDue.v1.schema.json) |
| AccessGranted | API | An admin creates an owner and their business, or an owner adds (or restores) a staff member. Not emitted when the owner adds themselves (decided 2026-11-19). | [AccessGranted.v1](events/AccessGranted.v1.schema.json) |
| PasswordResetRequested | API | Password recovery is requested for an existing account, outside the 5-minute cooldown. | [PasswordResetRequested.v1](events/PasswordResetRequested.v1.schema.json) |
| PasswordChanged | API | A user changes their password while logged in, or replaces an existing password through a recovery link. Not emitted when a recovery link sets the first password of an account that had none (decided 2026-11-19). | [PasswordChanged.v1](events/PasswordChanged.v1.schema.json) |

## Envelope

Every event uses the [envelope](events/envelope.v1.schema.json): `eventId`, `eventType`, `schemaVersion`, `occurredAt`, `correlationId` and `data`. Shared types (IDs, UTC timestamps, IANA time zones, actors, slots) are in [common.v1](events/common.v1.schema.json).

- Times are UTC with a `Z` suffix. Events carry the business's time zone so the worker can show both time zones (R2).
- `correlationId` is the ID of the request that caused the event, logged by the worker (specs/07-nfr.md).
- Events carry IDs, not names or email addresses. The worker looks up contact details when it sends, so personal data stays out of queues, the dead-letter queue and logs.
- A slot is the booked interval without buffers. Buffers are not needed by any consumer.
- [events/examples/](events/examples/) holds one example of each event the API publishes, exactly as the relay sends it (MySQL's canonical JSON, keys sorted). The API's tests regenerate and compare them, and the worker's tests validate them, so a change on either side that the other would reject fails a test (decided 2026-11-23).

## Versioning

- Each event type has an integer `schemaVersion`, and each version has its own schema file, `<EventType>.v<N>.schema.json`.
- Adding an optional field is not a breaking change and does not need a new version.
- Removing or renaming a field, changing its type or meaning, or making it required needs a new version.
- To release a new version, deploy a worker that accepts both versions first, then switch the API to the new version, then remove the old version once no messages with it remain in the queue or dead-letter queue.
- The worker rejects an unknown `eventType` or `schemaVersion` by letting the message go to the dead-letter queue.

## Delivery

- **Transactional outbox.** The API writes each event to an `outbox` table in the same transaction as the change that causes it. A relay publishes outbox rows to SQS after commit and marks them sent. This keeps network calls out of the transaction that holds the staff lock ([ADR 0002](../docs/adr/0002-booking-concurrency-row-lock.md)) and means an event is never lost if the API stops between commit and publish.
- **FIFO queue.** Events go to an SQS FIFO queue with the booking ID as the message group (the original booking's ID for RebookedInsideWindow, the user's ID for AccessGranted, PasswordResetRequested and PasswordChanged), so events for one booking are processed in order. For example, a reschedule is never processed before the booking's creation. The relay publishes each group's events in `outbox.seq` order, a database-assigned number, not by `created_at`, which comes from each API instance's clock (decided 2026-11-23, see [specs/05-data-model.md](05-data-model.md)).
- **At least once.** The relay may publish an event more than once. It uses the `eventId` as the SQS deduplication ID, which drops repeats within SQS's five-minute deduplication window ([ADR 0008](../docs/adr/0008-outbox-relay-in-api.md)). The worker sends each email at most once per event and recipient, using the unique key on `notifications` (see [specs/05-data-model.md](05-data-model.md)). Reminder scheduling is idempotent by schedule name.
- **Failures.** A message that fails after the queue's maximum receive count goes to the dead-letter queue. A CloudWatch alarm fires when the dead-letter queue is not empty.

## Worker handling

| Event | Emails | Reminders (R9) |
|---|---|---|
| BookingCreated | Confirmation to the customer. | Schedule the 24h and 1h reminders. Skip any whose time has already passed when the event is processed. |
| BookingRescheduled | Rescheduling email to the customer and the staff member. If the staff member changed, to both the old and new staff members. | Move the reminders to the new start. Skip any whose new time has already passed, and delete those. |
| BookingCancelled | Cancellation email to the customer and the staff member. | Delete pending reminders. |
| BookingLocalTimeChanged | Email to the customer with the booking's new local time in the business's time zone. | None. The UTC start is unchanged. |
| RebookedInsideWindow | Email to the original booking's staff member and to the business's owner, naming both bookings. | None. |
| AccessGranted | If `inviteId` is set: generate a random token, store its hash (lowercase hex SHA-256, see [specs/05-data-model.md](05-data-model.md)) and an expiry 7 days ahead on the `user_invites` row, and email the user a set-password link (kind Invite). Skip the invite (no token, no email) if the row's `used_at` is set or it is no longer the user's newest invite (by `created_at`, then the greater `id`), since its link could never work; the user has either accepted an invite or will get the newer one (decided 2026-11-18). If the email is sent again (a Pending or Failed `notifications` row), a new token replaces the old one, so only the latest link works. If `inviteId` is null: email the user that they now own the business or are staff in it (kind AccessGranted). The raw token is never put in a queue, log or event. | None. |
| PasswordResetRequested | Generate a random token, store its hash and an expiry 1 hour ahead on the `password_resets` row, and email the user a recovery link (kind PasswordReset). If the email is sent again (a Pending or Failed `notifications` row), a new token replaces the old one. The raw token is never put in a queue, log or event. | None. |
| PasswordChanged | Email the user that their password was changed (kind PasswordChanged), saying how (changed while logged in, or reset through a recovery link). | None. |
| ReminderDue | Reminder to the customer, only if the booking is still Confirmed and still starts at `scheduledForStartUtc`. Otherwise the reminder is stale and is dropped. | None. |

Reminder schedules are named from the booking ID and reminder type (e.g. `reminder-<bookingId>-24h`), so creating, moving and deleting them is idempotent. Each time the worker creates or moves a schedule, it gives the ReminderDue payload a new `eventId`, so a moved reminder is not mistaken for a duplicate of one already sent for the old time.

The worker records each email it sends, or fails to send, in `notifications`.
