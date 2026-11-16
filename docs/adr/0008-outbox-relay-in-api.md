# ADR 0008: Run the outbox relay as a background service in the API, on ECS Fargate

- **Status:** Accepted
- **Date:** 2026-11-16
- **Related specs:** [specs/06-events.md](../../specs/06-events.md), [specs/08-architecture.md](../../specs/08-architecture.md)
- **Related ADRs:** [ADR 0002](0002-booking-concurrency-row-lock.md)

## Context

The API writes events to the `outbox` table in the same transaction as the change (specs/06-events.md), and a relay publishes unpublished rows to the SQS FIFO queue after commit. The relay needs somewhere to run. specs/08-architecture.md left API hosting open ("ECS Fargate (or Lambda)"), and a background service needs a long-running host.

## Options considered

1. **Background service in the API** (chosen). Polls every second or two, so emails go out within seconds, with no extra deployable.
2. **Scheduled Lambda.** EventBridge runs it at most once a minute, so emails can lag by up to a minute; keeps API hosting open, but is another deployable inside the VPC.
3. **Separate always-on Fargate service.** Same latency as option 1 and keeps API hosting open, at the cost of a second always-on service.

## Decision

- The relay is an ASP.NET Core hosted service inside the API, polling the outbox every 1-2 seconds.
- The API therefore runs on ECS Fargate, not Lambda. This settles the API hosting decision for T27.
- Several API instances run the relay at once. Each claims rows with `SELECT ... FOR UPDATE SKIP LOCKED`, oldest first, publishes them, and sets `published_at`, so instances do not block each other or publish the same row concurrently.
- Delivery is at least once: if an instance publishes and then fails before committing `published_at`, the row is published again. The SQS deduplication ID is the event's `eventId`, and the worker's `notifications` table ignores a repeat, so a duplicate does not send a second email.

## Consequences

- No extra deployable; the relay is deployed, scaled and logged with the API.
- The API needs at least one running task at all times (no scale to zero).
- Relay work shares CPU with request handling; at the expected volume this is negligible.
- The relay is never inside a transaction that holds a staff lock (ADR 0002): it runs on its own connection after the API's transaction commits.
