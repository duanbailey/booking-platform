# ADR 0004: Copy buffers onto each booking when it is made

- **Status:** Accepted
- **Date:** 2026-11-13
- **Related rules:** R1, R5, R7 ([specs/03-business-rules.md](../../specs/03-business-rules.md))
- **Related ADRs:** [ADR 0002](0002-booking-concurrency-row-lock.md), [ADR 0003](0003-reschedule-keeps-booking-id.md)

## Context

R1 says a staff member's bookings can't overlap, buffers included. Buffers belong to a service, and an owner can change them at any time.

If a buffer change applied to existing bookings:

- **Bookings could start to clash, and clashes could cascade.** Longer buffers can make bookings that were valid overlap. Fixing one clash can push into the next booking, and so on.
- **Every clash would need resolving** by rejecting the change, cancelling bookings, or flagging them for someone to sort out.
- **The change would be expensive to make safely.** Under ADR 0002 it would have to lock every staff member who offers the service while it re-checks all their future bookings.
- **Customers would be affected** by a cancellation or move they did nothing to cause.

## Decision

When a booking is made, its blocked interval is worked out from the service's buffers at that moment and stored on the booking:

- `blocked_start_utc` = `start_utc` minus the before-buffer;
- `blocked_end_utc` = `end_utc` plus the after-buffer.

From then on, the booking's buffers never change, even if the service's buffers do. A buffer change only affects bookings made after it.

A reschedule (R5) works out the blocked interval again from the service's **current** buffers. This keeps a rescheduled booking consistent with the slots the customer was shown, which slot generation (R8) builds using current buffers.

## Consequences

- Changing a service's buffers needs no re-checking, locking or notifications. Existing bookings stay valid.
- The R1 and R7 conflict check reads only the bookings table: two bookings for the same staff member conflict when their blocked intervals overlap. No join to services is needed, and the check can use an index on staff member and blocked times.
- Bookings for the same service can have different buffers, depending on when each was made. Anything that needs a booking's buffers reads them from the booking, not the service.
- The bookings table gains two columns: `blocked_start_utc` and `blocked_end_utc` ([specs/05-data-model.md](../../specs/05-data-model.md)).
- Service duration already works this way, since `end_utc` is stored on the booking.
