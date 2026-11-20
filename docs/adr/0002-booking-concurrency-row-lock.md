# ADR 0002: Enforce booking concurrency with a row lock on the staff member

- **Status:** Accepted
- **Date:** 2026-11-13 (amended 2026-11-19: lock order with user rows)
- **Related rules:** R1, R5, R6, R7 ([specs/03-business-rules.md](../../specs/03-business-rules.md))

## Context

R7 says that when two requests arrive at the same time for overlapping time with the same staff member, buffers included, exactly one must succeed and the other must get an HTTP 409.

A conflict check done only in application code is not enough. Two requests can both check for conflicts, both find none, and both insert a booking. The result is a silent double booking.

An in-process concurrency primitive (a C# `lock` or `SemaphoreSlim`) does not fix this. It only works inside one process, and the API will run as more than one instance on AWS: behind a load balancer, when autoscaling, and while old and new versions overlap during a deploy. Requests that land on different instances never see each other's locks.

The bookings, buffers and time off that decide a conflict all live in MySQL, the one thing every instance shares. The lock has to be there too.

## Options considered

1. **Lock the staff member's row inside a transaction** (chosen).
2. **A distributed lock** (e.g. Redis). It works, but adds infrastructure and lock-expiry edge cases, all to coordinate access to data that already sits in MySQL.
3. **A database constraint on bookings.** Postgres can forbid overlapping time ranges outright, but MySQL has no such constraint. A unique key on staff member and start time only catches identical start times, not overlaps or buffers.
4. **A slot-reservation table with a unique constraint.** Each booking inserts one row per time block it covers, buffers included, into a reservation table with a unique key on staff member and block start. Any overlap then fails the insert, with no lock needed. Not chosen because:
   - one booking writes many rows (a 90-minute service with 15-minute buffers is 8 rows on a 15-minute grid), and every cancel, reschedule and time-off change must keep them in step with the booking;
   - the grid size limits which durations and buffers are possible; anything that doesn't line up with it has to be rounded, which wastes time or needs a finer grid with even more rows;
   - changing a business's grid or a service's buffers means rebuilding reservations for existing bookings.

   Row locking enforces the same guarantee on the bookings table alone, with no second copy of the schedule.

## Decision

Every operation that changes a staff member's schedule runs inside a database transaction and locks that staff member's row first:

1. Begin a transaction.
2. `SELECT … FROM staff WHERE id = ? FOR UPDATE` to lock the staff member's row.
3. Check for overlapping active bookings, buffers included (R1).
4. Write the change, then commit. If there is a conflict, roll back and return HTTP 409 (R7).

This applies to:

- creating a booking;
- rescheduling (R5): the booking is moved in place, in one transaction (see [ADR 0003](0003-reschedule-keeps-booking-id.md));
- confirming time off that cancels bookings (R6).

## Consequences

- A second request for the same staff member waits at the lock. When it gets through, it sees the first request's changes and returns 409 if they conflict.
- Requests for different staff members do not block each other.
- Requests for the same staff member are handled one at a time. This is acceptable at the expected booking volume per staff member.
- Transactions that hold the lock must stay short: no network calls or notifications inside them. Notifications are sent after the commit.
- If a reschedule ever moves a booking between staff members, both staff rows must be locked in a fixed order (e.g. by ID) to avoid deadlocks.
- **Lock order with user rows (added 2026-11-19).** Account operations lock the user's `users` row ([ADR 0010](0010-refresh-token-rotation.md): login, invites, password reset and change), and adding or restoring a staff member (T10) locks the user row and then the staff row. Any transaction that needs both takes the user row first, then the staff row, and never locks a user row while it already holds a staff row. Adding staff takes both, and so does creating a booking (T14), which locks the customer's and the booking user's rows in share mode (in id order) before the staff row, because the booking and history inserts' foreign keys would otherwise lock those rows after it (decided 2026-11-20). Removal (T20) and any later operation that also locks a user row must keep this order, or two such transactions can deadlock.
- R7 cannot be tested with in-memory fakes. Its test fires two simultaneous requests at a real MySQL instance (e.g. in a test container) and asserts exactly one success and one 409.
