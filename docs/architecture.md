# Architecture

How the booking platform's parts fit together on AWS. The sources are [specs/08-architecture.md](../specs/08-architecture.md), [specs/06-events.md](../specs/06-events.md), [specs/07-nfr.md](../specs/07-nfr.md) and ADRs [0002](adr/0002-booking-concurrency-row-lock.md), [0007](adr/0007-aws-testing-with-fakes.md), [0008](adr/0008-outbox-relay-in-api.md) and [0009](adr/0009-terraform-for-infrastructure.md). Dashed boxes are planned and not built yet; the task that builds each is in [plan/tasks.md](../plan/tasks.md). The AWS services themselves (queue, SES, Scheduler and the rest) are provisioned in T27; the code that talks to them sits behind interfaces with fakes (ADR 0007). Updated 2026-11-24 (T1-T26 built).

## Components

```mermaid
flowchart LR
    client["Clients<br/>(customers, staff, owners, admins)"]
    operator["Operator"]

    subgraph aws["AWS (Terraform, ADR 0009)"]
        alb["Load balancer<br/>trusted X-Forwarded-For"]

        subgraph ecs["ECS Fargate"]
            api["C# Web API<br/>REST /v1, JWT auth,<br/>rate limits, staff row locks"]
            relay["Outbox relay<br/>background service in the API<br/>(ADR 0008, built in T21)"]
            cli["Admin CLI<br/>one-off task, same image<br/>admin grant | revoke | list"]
        end

        db[("RDS MySQL<br/>bookings, staff, availability,<br/>outbox, notifications, ...")]
        secrets["Secrets Manager<br/>DB credentials, JWT key"]

        queue[["SQS FIFO queue<br/>message group = booking ID<br/>(user ID for account events)"]]
        dlq[["FIFO dead-letter queue"]]

        worker["Node worker<br/>Lambda, TypeScript<br/>(T23-T26), limited DB user<br/>(worker/db/worker-grants.sql)"]
        ses["SES<br/>emails"]
        scheduler["EventBridge Scheduler<br/>24h and 1h reminders (R9)"]

        logs["CloudWatch<br/>JSON logs with correlation IDs,<br/>DLQ-not-empty alarm"]
    end

    client -- "HTTPS" --> alb --> api
    operator -- "run task" --> cli
    api -- "change + outbox row,<br/>one transaction" --> db
    cli --> db
    relay -- "claim each group's earliest unpublished row<br/>by seq, FOR UPDATE SKIP LOCKED" --> db
    relay -- "publish, dedup ID = eventId" --> queue
    queue -- "after max receives" --> dlq
    queue -- "events" --> worker
    worker -- "idempotency, contact details,<br/>invite and reset token hashes" --> db
    worker -- "send" --> ses
    worker -- "create, move, delete<br/>reminder-{bookingId}-24h / -1h" --> scheduler
    scheduler -- "ReminderDue at the due time<br/>group = booking ID (T25)" --> queue
    api -. "reads" .-> secrets
    worker -. "reads" .-> secrets
    api -. "logs" .-> logs
    worker -. "logs" .-> logs
    dlq -. "alarm" .-> logs

    classDef planned stroke-dasharray: 5 5
    class queue,dlq,ses,scheduler,alb,secrets,logs,cli planned
```

The admin CLI already exists as a command in the API binary (T6); only running it as an ECS task is planned (T27). The API, MySQL, the outbox table and the outbox relay run locally today (Docker Compose for MySQL); the relay publishes only when `Outbox:QueueUrl` is set (it is off in Development without one, and required everywhere else), and its SQS publisher is checked against a real queue by a tagged test once one exists. Everything AWS-specific is provisioned in T27.

## From a request to an email

Creating a booking, as an example of every change that notifies someone.

```mermaid
sequenceDiagram
    autonumber
    actor C as Customer
    participant API as C# API
    participant DB as MySQL
    participant R as Outbox relay
    participant Q as SQS FIFO
    participant W as Node worker
    participant SES
    participant S as EventBridge Scheduler

    C->>API: POST /v1/bookings
    API->>DB: BEGIN, lock user rows, then the staff row (ADR 0002)
    API->>DB: check slot (R8), notice (R3), overlap (R1), rebook window (R4)
    API->>DB: INSERT booking, history row, outbox BookingCreated
    API->>DB: COMMIT
    API-->>C: 201 Created (or 409 SlotConflict / ConfirmationRequired)
    R->>DB: claim unpublished outbox rows (per group, in seq order)
    R->>Q: publish (group = booking ID, dedup = eventId)
    R->>DB: set published_at
    Q->>W: BookingCreated
    W->>DB: insert Pending notification (skip if already Sent)
    W->>DB: look up the customer's email
    W->>SES: confirmation email (both time zones, R2)
    W->>DB: mark notification Sent
    W->>S: schedule 24h and 1h reminders, skipping past times (R9)
    S->>Q: ReminderDue, 24h and 1h before the start
    Q->>W: ReminderDue: email the customer unless stale (cancelled, moved or started)
```

Delivery is at least once: a crash between publishing and setting `published_at` republishes the row, and the worker's `notifications` table, unique on (event, recipient), stops a second email ([ADR 0008](adr/0008-outbox-relay-in-api.md)). No network call happens inside the transaction that holds the staff lock. Each message group is published in `outbox.seq` order, and a row that fails is retried with backoff (1 s doubling to 60 s), holding back only its own group (decided 2026-11-23).

## Testing seams

Tests never need a live AWS account except the small tagged real-AWS suite ([ADR 0007](adr/0007-aws-testing-with-fakes.md)): the queue publisher, email sender and scheduler sit behind interfaces with in-memory fakes, and integration tests run against a real MySQL in Docker, because the R7 concurrency guarantee can only be tested there.
