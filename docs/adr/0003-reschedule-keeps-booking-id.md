# ADR 0003: Rescheduling keeps the booking ID

- **Status:** Accepted
- **Date:** 2026-11-13
- **Related rules:** R5 ([specs/03-business-rules.md](../../specs/03-business-rules.md))
- **Related ADRs:** [ADR 0002](0002-booking-concurrency-row-lock.md)

## Context

R5 originally described rescheduling as an "atomic cancel-and-rebook". That could be built two ways:

- **Keep the ID:** update the existing booking's slot in place.
- **New ID:** cancel the old booking and create a new one.

Atomicity is the same either way: both run in one transaction under the staff lock from ADR 0002. The difference is in everything that refers to a booking by its ID: emails, reminders, events and history.

## Options considered

1. **Keep the same ID** (chosen).
   - Links in earlier emails keep working.
   - The notification worker moves the reminders for one ID instead of cancelling and recreating them.
   - One `BookingRescheduled` event maps to a single "your booking has moved" email.
   - Reports don't count a reschedule as a cancellation plus a new booking.
   - Downside: the old slot is overwritten, so history must be recorded separately.
2. **New ID.**
   - Matches the original R5 wording, keeps the old booking as a cancelled row, and reuses the cancel and create code.
   - Downsides:
     - Links in earlier emails open a cancelled booking.
     - If an event goes missing, the old booking's reminders can go stale.
     - The worker could send "cancelled" and "booked" emails instead of one "rescheduled" email.
     - A `rescheduled_from_id` link is needed to trace history.
     - Cancellation counts go up unless reports filter reschedules out.

## Decision

A reschedule updates the existing booking in place, and the booking ID never changes.

Each reschedule also writes a row to a booking history table, recording:

- the booking ID;
- the old start time, end time and staff member;
- the new start time, end time and staff member;
- who made the change;
- when it was made.

The update and the history row are written in the same transaction, under the staff lock from ADR 0002.

The reschedule emits one `BookingRescheduled` event ([specs/06-events.md](../../specs/06-events.md)) carrying the booking ID and the old and new times. It does not also emit `BookingCancelled` or `BookingCreated`.

## Consequences

- R5 is reworded to describe a move rather than a cancel-and-rebook.
- The data model ([specs/05-data-model.md](../../specs/05-data-model.md)) needs a booking history table.
- The notification worker handles `BookingRescheduled` by moving the booking's 24h and 1h reminders and sending a single rescheduling email.
- The conflict check for the new slot must ignore the booking's own current slot, so a booking can be moved to an overlapping time (e.g. 30 minutes later).
- If a booking's history matters to a decision (e.g. support or auditing), it is read from the history table, not from the booking row.
