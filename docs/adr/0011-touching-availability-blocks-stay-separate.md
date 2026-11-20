# ADR 0011: Touching availability blocks stay separate instead of being merged

- **Status:** Accepted
- **Date:** 2026-11-20
- **Related rules:** R6, R8 ([specs/03-business-rules.md](../../specs/03-business-rules.md))

## Context

A staff member's availability on a date is a set of blocks: weekly blocks, or a date exception's ranges (R8). Two blocks can touch, e.g. 10:00-12:00 and 12:00-13:00. Slot generation must decide whether an appointment may span the point where they meet. The same question comes up when an availability change is checked for bookings it would leave outside availability (R6, T19).

## Options considered

1. **Merge touching blocks into one continuous period** before generating slots or checking bookings. A 60-minute service is then offered at 11:30 above, and splitting 09:00-17:00 into 09:00-12:00 and 12:00-13:00 changes nothing. Not chosen for now: it adds merging logic to slot generation and to the R6 check, and it takes away the owner's way to say "nothing across this point" without leaving a gap.
2. **Each block bounds the appointment on its own** (chosen). An appointment must fit inside a single block, even when blocks touch.

## Decision

Each availability block bounds the appointment on its own, even when two blocks touch. With blocks 10:00-12:00 and 12:00-13:00, a 60-minute service is not offered at 11:30. An owner who wants one continuous period enters one block. The same rule decides whether a change to availability leaves a booking outside it (R6).

## Consequences

- What the owner enters is exactly what applies. The rule is simple to explain and to test, and slot generation and the R6 check share it.
- An owner who splits a period into touching blocks offers fewer slots across the join, possibly without realising why.
- Splitting an existing period (T19) flags a booking that spans the new join, e.g. 11:30-12:30 when 09:00-17:00 becomes 09:00-12:00 and 12:00-17:00. Under R6 the owner is warned and shown that booking; it is cancelled only if they confirm, so nothing is lost silently.

## Later enhancement (out of scope)

Merging touching blocks (option 1), or warning an owner when they save touching blocks, could be added later if owners find the current behaviour surprising. It is not part of the current scope. Adding it would change R8 and the R6 check together, would need the slot generator and the T19 availability check to merge blocks the same way, and would offer slots across joins that are not offered today.
