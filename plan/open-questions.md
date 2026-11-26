# Open questions

Optional spec items that are still open. Decisions are recorded in `specs/`, `docs/adr/` and the "Decided" list in `plan/tasks.md`; this file lists only what is still open. Remove items as they are resolved.

## Status

Updated 2026-11-24. Since T3, each task's decisions go into the "Decided" list in `plan/tasks.md` (or `specs/` and an ADR), and progress is the ticked tasks there; this file is no longer a status page. Nothing here blocks a task: the only open items are the two optional wording suggestions and the request body size limit (added 2026-11-25) under "Smaller items". The sections between, from the 2026-11-15 and 2026-11-16 reviews, are kept for reference (the 2026-11-15 review was skipped by the user).

## Schema choices to confirm (T2, 2026-11-16)

Confirmed on 2026-11-16 and written into `specs/05-data-model.md`: the extra columns, case-insensitive email, same-business rules enforced by the API only, and `platform_admins.granted_by` as a nullable reference to users (null only for the bootstrap admin, no seeded account).

Smaller choices with no objection raised, kept in the migration: IDs are `char(36)` UUIDs; timestamps `datetime(6)` UTC, rejected by EF if not `DateTimeKind.Utc`; database checks for non-empty ranges, blocked interval containing the booking, cancellation columns all-or-nothing, and history `previous_*` only for Rescheduled; rules spanning rows (overlapping availability ranges, null row alongside ranges) checked by the API in T11; `notifications.event_id` has no foreign key (reminder events do not come from the outbox); no `business_id` on bookings; every foreign key is `RESTRICT`; T1's empty `Initial` migration was replaced by `InitialSchema`.

## Spec changes on 2026-11-15 (for review)

Made while settling the API outline:

- **Requirements:** new stories for customer sign-up, admin creating an owner and their business, invited users setting a password (including resending), owner setting up their business, adding and removing staff, and staff or owners booking on behalf of a customer. The duplicate service stories were merged, and typos fixed.
- **R4:** the rebook warning now applies to any booking made for a customer, including by staff or an owner on their behalf.
- **R6:** removing a staff member with bookings that have not started uses the warn-and-confirm flow, with the cancellation reason StaffRemoved.
- **R8:** a booking or reschedule must start at a time slot generation would offer.
- **Data model:** new tables `platform_admins` and `user_invites`; `password_hash` is null until an invite is accepted; staff rows get `removed_at`; `booking_history` records each booking's creation and who made it; new cancellation reason StaffRemoved; new notification kinds Invite and AccessGranted.
- **Events:** new `AccessGranted` event; `StaffRemoved` added to `BookingCancelled.v1` (safe because nothing is deployed yet).
- **Authorization:** admin role (create owners, resend owners' invites, nothing inside a business); customers reschedule with the same staff member only.

## Smaller items

- **specs/08-architecture.md:** one wording suggestion, left for the owner to decide: "SQS plus a dead-letter queue, FIFO queue" reads as two queues (it is one FIFO queue, and its dead-letter queue must also be FIFO). (The "(or Lambda)" conflict was resolved by ADR 0008 on 2026-11-16.)
- **specs/01-overview.md:** glossary terms are listed but not defined; typo "caldendar". Newer terms worth adding: cancellation window, minimum notice, advance-booking window, date exception, admin, invite.
- **Request body size limit (raised in T30, 2026-11-25):** nothing caps a request body except Kestrel's default of 30 MB, and since T30 a JSON body is held in memory once to check its syntax. Real bodies are a few hundred bytes. A lower limit (e.g. 64 KB, answered with the existing 413 `RequestRejected`) would bound memory per request; it is a behaviour change, so it is for the owner to decide.

## Suggested order

1. ~~Data model gaps.~~ Done.
2. ~~Reminder rule (R9) and the remaining requirements scenarios.~~ Done.
3. ~~Events, API and authorization rules.~~ Done.
4. ~~Task plan.~~ Done: `plan/tasks.md`.
