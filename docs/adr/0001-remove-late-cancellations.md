# ADR 0001: Remove late cancellations

- **Status:** Accepted
- **Date:** 2026-11-13
- **Related rules:** R4, R6 ([specs/03-business-rules.md](../../specs/03-business-rules.md))

## Context

The original R4 said customers could cancel "free of charge" up to N hours before a booking. A cancellation after that point would still go through, but the booking would be marked as a **late cancellation**.

Payments are a non-goal ([specs/01-overview.md](../../specs/01-overview.md)). The platform never charges customers, so "free of charge" had no meaning and a late-cancellation mark had nothing to trigger: no fee, no refund rule, no penalty. Keeping it would have added a booking status and extra logic with nothing to show for it.

## Decision

Remove the concept of late cancellation. Enforce the cancellation window by blocking cancellation instead:

- Customers may cancel up to N hours before the start. The window is inclusive: cancelling exactly N hours before is allowed.
- Less than N hours before the start, customers cannot cancel or reschedule. To change time, they must book a new slot.
- Staff and owners can still cancel at any time.
- N is set per business, with a default of 24 hours.
- Bookings cancelled because staff or an owner blocked time off (R6) are business-initiated, so the R4 window does not apply to them.

## Consequences

- The booking state model has no `LateCancelled` status. A cancelled booking is simply `Cancelled`.
- The cancel and reschedule endpoints must reject a customer request inside the window with a clear error. Both need acceptance criteria in [specs/02-requirements.md](../../specs/02-requirements.md).
- Blocking rescheduling as well stops customers getting round the cancellation block by moving a booking instead.
- A customer who cannot attend inside the window has to contact the business, which can cancel on their behalf.
- If payments are ever brought into scope, revisit this decision: late-cancellation fees would bring back the need to tell late and on-time cancellations apart.

## Follow-up: the original booking when a customer books again inside the window

*Decided 2026-11-13.*

Inside the window, a customer who wants a different time must book a new slot, but cannot cancel the original booking. They then hold two bookings, and the original stays on the staff member's calendar unless the business cancels it.

Cancelling the original automatically was rejected. A customer could book any slot further out than N hours, which cancels the original, then cancel the new booking outside the window. That is a free late cancellation, which defeats the window.

Without payments, the window cannot stop a customer abandoning a booking; they can simply not turn up. What it controls is whether the slot is released and whether the business is told. Two bookings the business does not know about is the worst outcome for both, so the fix is to tell people:

- Before the customer confirms the new booking, they are warned that the original still stands and that they must contact the business to cancel it.
- Once the new booking is made, the original booking's staff member and the business's owner are notified that the customer holds both bookings, so they can cancel the original.

The new booking goes ahead and the original is kept. No booking states are added, and the window is enforced exactly as decided above. The rule is in R4.

## Note: bookings made on a customer's behalf

*Added 2026-11-15.*

The follow-up above describes a customer booking again for themselves. R4 now covers any booking made for a customer who already holds a booking inside the window with that business, including one a staff member or owner makes on the customer's behalf. In every case the new booking goes ahead, the original is kept, the person making the booking is warned that the original still stands, and the original booking's staff member and the business's owner are notified. Only a customer booking for themselves is also told to contact the business to cancel the original. The reasoning above is unchanged; see R4 for the current rule.
