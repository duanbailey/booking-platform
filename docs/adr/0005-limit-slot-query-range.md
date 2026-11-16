# ADR 0005: Limit slot queries to 30 days instead of capping the advance-booking window

- **Status:** Accepted
- **Date:** 2026-11-13
- **Related rules:** R8 ([specs/03-business-rules.md](../../specs/03-business-rules.md))
- **Related specs:** [specs/07-nfr.md](../../specs/07-nfr.md)

## Context

The performance target in specs/07-nfr.md is a slot query p95 under 300 ms for a 30-day window. The maximum advance-booking window (R8) is a per-business setting with a default of 3 weeks and no upper limit. A business could set it to 90 days or more, and a customer could then ask for slots across the whole window, which the target does not cover.

## Options considered

1. **Cap the advance-booking window at 30 days.** The target then covers the worst case. Not chosen: it limits what businesses can offer (e.g. a clinic booking three months ahead) to fit a performance target, when the cost comes from the size of one query, not from how far ahead bookings are allowed.
2. **Limit the range of a single slot query to 30 days** (chosen). The advance-booking window stays uncapped. A customer looking further ahead pages through the window in ranges of up to 30 days.

## Decision

A slot query's date range can cover at most 30 days. A request for a longer range is rejected with HTTP 400, and the error states the maximum. The advance-booking window has no upper limit.

A range that extends past the advance-booking window, or into the past, is not an error: R8 already means no slots are returned for those times.

## Consequences

- The 300 ms target covers every valid slot query, whatever a business's advance-booking window.
- The API spec ([specs/04-api.openapi.yaml](../../specs/04-api.openapi.yaml)) must document the 30-day limit and the 400 response on the slot query endpoint.
- A client showing more than 30 days (e.g. a three-month calendar) makes several queries.
- If the limit is ever raised, the performance target must be raised with it, and tested again.
