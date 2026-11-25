# Architecture

How the booking platform's parts fit together on AWS. The sources are [specs/08-architecture.md](../specs/08-architecture.md), [specs/06-events.md](../specs/06-events.md), [specs/07-nfr.md](../specs/07-nfr.md) and ADRs [0002](adr/0002-booking-concurrency-row-lock.md), [0007](adr/0007-aws-testing-with-fakes.md), [0008](adr/0008-outbox-relay-in-api.md) and [0009](adr/0009-terraform-for-infrastructure.md). Everything shown is built and deployed (dev environment in us-east-2, Terraform in [`infra/`](../infra)); operating it is in the [runbook](runbook.md). Updated 2026-11-25 (T27c, first deployment).

## Components

```mermaid
flowchart LR
    client["Clients<br/>(customers, staff, owners, admins)"]
    operator["Operator<br/>infra/deploy.sh, admin tasks"]

    subgraph aws["AWS us-east-2 (Terraform, ADR 0009)"]
        cdn["CloudFront<br/>*.cloudfront.net HTTPS,<br/>no caching, HTTP to HTTPS"]

        subgraph vpc["VPC, two Availability Zones"]
            subgraph private["Private subnets"]
                alb["Internal load balancer<br/>reached only through the<br/>CloudFront VPC origin"]

                subgraph ecs["ECS Fargate"]
                    api["C# Web API<br/>REST /v1, JWT auth,<br/>rate limits per client IP,<br/>staff row locks"]
                    relay["Outbox relay<br/>background service in the API<br/>(ADR 0008)"]
                    oneoff["One-off tasks, same image<br/>db migrate | admin grant, revoke, list"]
                end

                db[("RDS MySQL 8.4<br/>users booking_api (data only),<br/>booking_worker (worker-grants.sql)")]
                worker["Node worker<br/>Lambda, TypeScript,<br/>booking_worker user"]
            end
            nat["NAT gateway<br/>(public subnet)"]
        end

        secrets["Secrets Manager<br/>DB passwords, JWT key,<br/>RDS-managed master password"]
        queue[["SQS FIFO events queue<br/>group = booking ID<br/>(user ID for account events)"]]
        dlq[["FIFO dead-letter queue"]]
        ses["SES<br/>one verified sender, sandbox"]
        scheduler["EventBridge Scheduler<br/>24h and 1h reminders (R9)"]
        sdlq[["Reminders dead-letter queue"]]
        logs["CloudWatch<br/>JSON logs, outbox-age metric (EMF),<br/>alarms to SNS email"]
    end

    client -- "HTTPS" --> cdn -- "HTTP, VPC origin" --> alb --> api
    operator -- "run task" --> oneoff
    api -- "change + outbox row,<br/>one transaction" --> db
    oneoff --> db
    relay -- "claim each group's earliest unpublished row<br/>by seq, FOR UPDATE SKIP LOCKED" --> db
    relay -- "publish, dedup ID = eventId" --> queue
    queue -- "after 5 receives" --> dlq
    queue -- "events, batches of 10" --> worker
    worker -- "idempotency, contact details,<br/>invite and reset token hashes" --> db
    worker -- "send" --> ses
    worker -- "create, move, delete<br/>reminder-{bookingId}-24h / -1h" --> scheduler
    scheduler -- "ReminderDue at the due time<br/>(content-based dedup)" --> queue
    scheduler -- "undeliverable for 1 hour" --> sdlq
    api -. "AWS calls via" .-> nat
    worker -. "AWS calls via" .-> nat
    api -. "reads at start" .-> secrets
    worker -. "reads at start" .-> secrets
    api -. "logs, metric" .-> logs
    worker -. "logs" .-> logs
    dlq -. "alarm" .-> logs
    sdlq -. "alarm" .-> logs
```

Network: the load balancer, API tasks, worker Lambda and database have no public addresses. CloudFront reaches the internal load balancer through a VPC origin, and the load balancer's security group admits only CloudFront's origin-facing prefix list. The API and the worker reach SQS, SES, Scheduler and Secrets Manager through one NAT gateway, and the database accepts connections only from the API's and the worker's security groups.

Client IPs: the chain is viewer, CloudFront, load balancer, API. The API trusts the VPC range and CloudFront's origin-facing ranges for `X-Forwarded-For`, two hops back, so rate limits count per viewer and a forged header is ignored (checked on the first deployment, T27c).

IAM, least privilege per role: the API task role may send to the events queue; the worker may send email from the verified sender, create, update and delete schedules in the reminders group, pass the Scheduler role, and read its database secret; the Scheduler role may send to the events queue; the one-off `db migrate` task's execution role may read the RDS master secret and the two users' passwords. The CI role, assumed by GitHub Actions on `main` through OIDC (T27d), may only do what the real-AWS tests do: use the SQS test queue, send from the SES sender, and manage schedules in the reminders group. Database users: `booking_api` has SELECT, INSERT, UPDATE and DELETE (no schema changes); `booking_worker` has exactly the column-level grants in [`worker/db/worker-grants.sql`](../worker/db/worker-grants.sql), which cannot read `password_hash`; both are created and kept in step by `db migrate` before each rollout.

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
