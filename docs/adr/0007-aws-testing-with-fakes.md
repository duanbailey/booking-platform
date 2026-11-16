# ADR 0007: Test AWS calls with in-memory fakes plus a small real-AWS suite

- **Status:** Accepted
- **Date:** 2026-11-16
- **Related rules:** R9 ([specs/03-business-rules.md](../../specs/03-business-rules.md))
- **Related specs:** [specs/06-events.md](../../specs/06-events.md), [specs/08-architecture.md](../../specs/08-architecture.md)

## Context

The outbox relay publishes to an SQS FIFO queue, and the Node worker sends email through SES and creates, moves and deletes reminders in EventBridge Scheduler. These calls need tests that run on every change without a live AWS account.

The design keeps the logic that must be correct in MySQL, not AWS: the outbox decides what is published, `notifications` stops duplicate emails, and a stale `ReminderDue` is dropped by checking the booking. The AWS calls themselves are thin.

## Options considered

1. **In-memory fakes behind small interfaces, plus a few tests against real AWS** (chosen).
2. **LocalStack.** Real SDK calls against an AWS imitation in Docker, runnable offline. Not chosen: its fidelity varies by service (EventBridge Scheduler is a likely weak spot for R9), it cannot catch IAM or SES sandbox problems, and its licensing has been changing.
3. **Both.** The most coverage, but three test setups to maintain.

## Decision

- Each AWS call sits behind a small interface: a message publisher (API, for the relay), an email sender and a reminder scheduler (worker). Most tests use in-memory fakes.
- Fakes model the AWS behaviour the code relies on, where it matters: FIFO message group and deduplication ID on publish, and a schedule name that already exists on create.
- A small suite of real-AWS tests, tagged so it runs separately, checks each adapter against a dev AWS account: publish to and receive from a FIFO queue, send an SES email (sandbox, verified address), and create, update and delete a schedule.
- The real-AWS suite runs in CI with credentials for the dev account only. Ordinary test runs (`dotnet test`, `npm test`) never need AWS credentials.

## Consequences

- Ordinary test runs stay fast and offline, with only MySQL in Docker.
- A fake can only behave as we believe AWS does; the real-AWS suite is the check on that belief and must be kept small but run regularly.
- CI needs a dev AWS account and credentials limited to the test resources (T27).
- Nothing tests the full booking-to-email flow offline; the real-AWS suite and a deployed environment cover that.
