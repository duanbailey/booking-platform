# Runbook

How to deploy, operate and tear down the dev environment on AWS.

> **Status (2026-11-25): torn down** to save the account's Free plan credits; only the bootstrap stack (state bucket, ECR repository) remains. To bring it back, follow "Deploying" (the first deploy creates everything), then the first-deployment steps: confirm the SES and SNS emails, make a bootstrap admin, and set the GitHub repository variables again from `terraform -chdir=infra output ci_repository_variables` (`AWS_CI_ROLE_ARN` was deleted so the real-AWS job skips meanwhile). The infrastructure is in [`infra/`](../infra) (Terraform, [ADR 0009](adr/0009-terraform-for-infrastructure.md)); how the parts fit together is in [architecture.md](architecture.md). Written after the first deployment (T27c, 2026-11-25).

## The environment

| | |
|---|---|
| Account, region | the dev account (`aws sts get-caller-identity --query Account --output text`), us-east-2 (AWS profile `booking-dev`, IAM user `booking-deployer`) |
| API | `terraform -chdir=infra output -raw api_url` (CloudFront, `https://<id>.cloudfront.net`); `GET /health` answers `{"status":"ok"}` without touching the database |
| Names | everything is prefixed `booking-platform-dev` (cluster, service, queues, Lambda, alarms) |
| Logs | API and one-off tasks: CloudWatch log group `/booking-platform/dev/api` (stream prefixes `api`, `migrate`); worker: `/aws/lambda/booking-platform-dev-worker` |
| State | main stack in S3 (`booking-platform-tfstate-<account-id>-us-east-2`, from the bootstrap output `state_bucket`; native locking); the bootstrap stack's state is the local, git-ignored `infra/bootstrap/terraform.tfstate`: keep it |
| Cost | about $75-80 a month while it runs; the NAT gateway, RDS, the load balancer and Fargate are most of it |

The account is on the **AWS Free plan**. It caps RDS backup retention (so backups are kept 1 day, not 7), and AWS closes a Free plan account when its credits run out or after 6 months unless it is upgraded to a paid plan in the Billing console. Check the remaining credits in Billing now and then.

### Tools

Docker, Terraform (>= 1.11), the AWS CLI v2, Node 22+ and npm, and Bash (Git Bash on Windows). `infra/dev.tfvars` (git-ignored, copied from `dev.tfvars.example`) holds the sender address (`email_from`) and the alarm address (`alarm_email`).

On Windows with Git Bash:

- Terraform and the AWS CLI installed with winget may not be on Git Bash's PATH; prepend `/c/Program Files/Amazon/AWSCLIV2` and the Terraform package folder under `%LOCALAPPDATA%/Microsoft/WinGet/Packages/`.
- Git Bash rewrites arguments that look like paths, so `aws logs ... --log-group-name /booking-platform/dev/api` fails with a validation error. Prefix such commands with `MSYS_NO_PATHCONV=1`.

```bash
export AWS_PROFILE=booking-dev
```

## Deploying

```bash
infra/deploy.sh plan   # build the worker package and show what would change; no image push
infra/deploy.sh        # deploy the current commit
```

`deploy.sh` refuses a dirty working tree (images are tagged by commit). It builds and pushes the API image, builds the worker package, then:

1. applies with the `db migrate` task pointing at the new image (the API keeps its current image);
2. runs `db migrate` as a one-off ECS task and waits for it (a failure stops the deploy; the API is untouched);
3. applies again, pointing the API service at the new image, and waits until the service is stable;
4. prints the API URL and calls `/health`.

Each apply shows its plan and asks for confirmation (`AUTO_APPROVE=1` skips that, for CI). Without a terminal (e.g. an automated agent), run the steps by hand: source the script to get its functions (`source infra/deploy.sh; build_worker_package; init_main; run_migrate`), save each plan with `terraform -chdir=infra plan -var-file=dev.tfvars -var api_image=... -var migrate_image=... -out=...`, review it, then `terraform apply` that saved plan.

The worker Lambda is updated by the same applies (its package comes from the same commit, so the grants `db migrate` applies match the worker's queries).

**Migrations** are never squashed again now that an environment exists. `db migrate` is safe to rerun: it applies pending migrations, then creates or updates the `booking_api` user (data-only rights) and the `booking_worker` user (from `worker/db/worker-grants.sql`), leaving a user alone when its privileges already match.

### First deployment notes

On a brand-new environment the API service starts before `db migrate` has created its database user, so the outbox relay logs a stream of errors (once a second) until migrate finishes, and the relay-error alarm may fire once and clear. The first deployment saw this for about 3.5 minutes. The cleanup's startup run fails the same way and logs one error; it runs again an hour later (see Cleanup).

AWS emails the sender address a verification link and the alarm address an SNS subscription confirmation on the first apply. Nothing is sent until the sender is verified; alarms reach nobody until the subscription is confirmed. Check with:

```bash
aws sesv2 list-email-identities
aws sns list-subscriptions-by-topic --topic-arn "$(terraform -chdir=infra output -raw alarm_topic)"
```

## Admin tasks

The admin CLI (`admin grant|revoke|list`) runs as a one-off ECS task from the API's current task definition, with the command overridden. The task uses the `booking_api` database user. Audit lines go to the task's log stream in `/booking-platform/dev/api`.

```bash
CLUSTER=$(terraform -chdir=infra output -raw cluster)
SERVICE=$(terraform -chdir=infra output -raw api_service)
SUBNETS=$(terraform -chdir=infra output -json private_subnets | tr -d '[]" \n')
SG=$(terraform -chdir=infra output -raw api_security_group)
TASK_DEF=$(aws ecs describe-services --cluster $CLUSTER --services $SERVICE --query 'services[0].taskDefinition' --output text)

aws ecs run-task --cluster $CLUSTER --launch-type FARGATE --task-definition $TASK_DEF \
  --network-configuration "awsvpcConfiguration={subnets=[$SUBNETS],securityGroups=[$SG],assignPublicIp=DISABLED}" \
  --overrides '{"containerOverrides":[{"name":"api","command":["admin","list"]}]}'
# then: aws ecs wait tasks-stopped ...; check containers[0].exitCode with aws ecs describe-tasks
```

Commands (the container's entry point is `dotnet BookingPlatform.Api.dll`):

- `["admin","grant","<email>"]` makes the first (bootstrap) admin; only allowed while there are no admins. The account must exist, so sign up through `POST /v1/auth/register` first (sign-up sends no email, so any address works).
- `["admin","grant","<email>","--granted-by","<admin email>"]` and `["admin","revoke","<email>"]` once an admin exists.
- `["admin","list"]`.

The first deployment's bootstrap admin is an `example.test` account; its password is with the person who ran T27c.

## Email (SES sandbox)

SES sends from the one verified address in `dev.tfvars` and stays in the **sandbox**: mail is delivered only to verified addresses, at most 200 a day. To let a tester receive mail, verify their address in the SES console (Configuration > Identities > Create identity > Email address); they click the link AWS sends. Mail to an unverified address fails at SES: the worker marks that notification Failed, retries the message, and it ends in the dead-letter queue.

Invite and recovery links point at `APP_BASE_URL`, which defaults to the API's CloudFront URL. There is no frontend yet, so those links don't open anything: take the `token` from the link and call `POST /v1/auth/accept-invite` or `POST /v1/auth/reset-password` directly.

Leaving the sandbox needs a domain (a verified domain identity with DKIM) and a production access request; see the plan.

## Alarms and what to do

All alarms go to the SNS topic `booking-platform-dev-alarms` (email to `alarm_email`), on both ALARM and OK.

| Alarm | Means | Do |
|---|---|---|
| `booking-platform-dev-events-dlq-not-empty` | an event failed 5 times and was parked in `booking-platform-dev-events-dlq.fifo` | Find the cause in the worker log (search for the eventId; errors name it, never the body). Fix, then redrive from the SQS console (DLQ > Start DLQ redrive) or delete the message. A redriven event is safe: the worker's `notifications` table stops duplicate emails. |
| `booking-platform-dev-reminders-dlq-not-empty` | EventBridge Scheduler could not deliver a reminder to the events queue within an hour | Read the message (it holds the reminder's input); check the Scheduler role and the queue policy. A reminder past its time is not worth resending. |
| `booking-platform-dev-outbox-stuck` | the oldest unpublished outbox row is over 5 minutes old, or the API stopped reporting the metric | Check the API service is running and its log for relay errors; a stuck group points at one failing row (`outbox.attempts`, `last_error`). Missing data also alarms: no API task, or the database is down. |
| `booking-platform-dev-relay-errors` | the outbox relay logged errors (failed publishes or rounds) | Read the errors in `/booking-platform/dev/api`. Expected once on a brand-new environment (see above). |
| `booking-platform-dev-worker-errors` | the worker Lambda crashed or could not start | Read the worker log; a failed start is usually configuration (env vars, the database secret, the grants). |

Known worker failure modes that end in the DLQ:

- A business or user time zone that NodaTime (the API) accepts but the Lambda's Node ICU data doesn't know fails formatting (T24). Rare (newly added zones); the fix is a Node runtime with newer ICU data.
- A permanent SES rejection (an unverified recipient in the sandbox) means no confirmation and no reminders for that booking (T25).

## Cleanup

Each API task runs a cleanup once at startup and then hourly (T31): it deletes outbox rows published more than 30 days ago and idempotency keys more than 24 hours old, 1000 rows per statement. A run that deleted something logs `Cleanup deleted ...` with the counts. A failed run logs an error (category `BookingPlatform.Api.Maintenance.DataCleanupService`) and is tried again an hour later; no alarm watches it, so if the outbox keeps growing, search the API log for that category. Settings: `Cleanup:IntervalMinutes` (default 60, 1-1440), `Cleanup:BatchSize` (default 1000, 1-10000), and `Cleanup:Enabled` (default true; tests turn it off). A value out of range stops the API at startup, naming the setting.

On a brand-new environment the startup run fails, because the API starts before `db migrate` has created its database user (see First deployment notes): it logs one cleanup error, and the next run an hour later succeeds. Nothing to do.

## Secrets

The JWT signing key and the two database users' passwords are generated by Terraform as write-only values and never appear in state. To rotate all three, bump `secret_version` (in `dev.tfvars` or with `-var secret_version=2`) and deploy: `db migrate` sets the new database passwords and keeps the old ones valid until its next run, so running tasks keep working while the service rolls. Rotating the JWT key invalidates every access token at once; clients get a 401 and refresh (refresh tokens are random values checked against the database, ADR 0010, so nobody has to log in again).

The RDS master password is managed by RDS in Secrets Manager; only the `db migrate` task reads it.

## Client IPs and rate limits

The auth rate limits (specs/07-nfr.md) count per client IP. The request chain is viewer → CloudFront → internal load balancer → API, and CloudFront reaches the load balancer from its public origin-facing addresses, so the API trusts the VPC range plus the CIDRs of AWS's `com.amazonaws.global.cloudfront.origin-facing` prefix list (`ForwardedHeaders__KnownNetworks__*`), walking back two hops (`ForwardLimit` 2). Each apply refreshes that list from AWS; deploy now and then so a range AWS adds is trusted.

To check it after a change: from one machine, send eleven failed logins, each with a different forged `X-Forwarded-For`; the eleventh must be a 429 (forged headers are ignored; the window resets on a timer, so a burst can straddle a reset: repeat if it doesn't). Right after, one failed login from another network (e.g. AWS CloudShell) must be a 401, not a 429 (the limit is per viewer). Before the fix every viewer shared one bucket.

## Known issue: occasional 30-second stalls

Open since the first deployment (2026-11-25). A small share of requests through CloudFront stall for about 10, 20 or 30 seconds; a 30-second one ends in a CloudFront 504. Probes measured 2 of 499 requests (17:05-17:10 UTC), 3 of 900 (17:21-17:26) and 0 of 892 (17:33-17:37); both health checks and logins were hit. The multiples of 10 s match CloudFront's origin connection timeout (10 s, up to 3 attempts), and the load balancer never saw the failing requests (no 5xx, slowest target response 2.3 s), so CloudFront intermittently fails to open a new connection to the load balancer.

Ruled out: the security group (also admitting CloudFront's VPC-origin group changed nothing); routes and network ACLs (identical in both zones); a broken load balancer node (flow logs showed CloudFront connecting through both zones' nodes; traffic sticking to one zone came from CloudFront reusing one keep-alive connection for minutes). The stalls didn't recur while flow logs were on, so it is not known whether the failing SYN reaches the VPC.

To investigate further: turn on VPC flow logs (all traffic, 1-minute aggregation), probe until a request takes over 10 s, then look for the connection on CloudFront's VPC-origin network interfaces (description "CloudFront configured ENI") and the load balancer's. No SYN in the VPC means the problem is inside CloudFront's VPC-origin service, a question for AWS support; a SYN without an answer means the load balancer side.

## Real-AWS tests

The tagged real-AWS tests ([ADR 0007](adr/0007-aws-testing-with-fakes.md)) run against this environment. With `AWS_PROFILE=booking-dev` and `AWS_REGION=us-east-2`:

```bash
# SQS publisher: a queue no one consumes (not the events queue, whose worker would take the messages)
BOOKING_TEST_SQS_QUEUE_URL=$(terraform -chdir=infra output -raw sqs_test_queue_url) \
  dotnet test BookingPlatform.slnx --filter Category=RealAws

# SES and Scheduler (worker): from worker/, with the values from `terraform -chdir=infra output reminder_settings`
BOOKING_TEST_SES_FROM=<sender> BOOKING_TEST_SES_TO=<a verified address> \
BOOKING_TEST_SCHEDULER_GROUP=... BOOKING_TEST_SCHEDULER_TARGET_ARN=... \
BOOKING_TEST_SCHEDULER_ROLE_ARN=... BOOKING_TEST_SCHEDULER_DLQ_ARN=... \
  npx jest src/email.test.ts src/scheduler.test.ts
```

A normal run skips these (two in the worker, one in C#); check the summary shows them passed, not skipped. The worker run needs the local test MySQL (`docker compose up -d --wait`), because Jest's global setup builds a schema.

## CI

GitHub Actions ([`.github/workflows/ci.yml`](../.github/workflows/ci.yml), T27d) runs the C# unit, C# integration and worker suites on every push, and the real-AWS tests on pushes to `main` and on manual runs (Actions > CI > Run workflow, branch `main`). The real-AWS job assumes the role `booking-platform-dev-ci` through GitHub's OIDC provider (`infra/ci.tf`); only `refs/heads/main` of `github_repository` (set in `dev.tfvars`) may assume it, and it may only use the SQS test queue, send from the SES sender, and manage schedules in the reminders group. `github_repository` is written as in GitHub's OIDC subject claim, which includes the immutable owner and repository IDs: `owner@<owner id>/<name>@<repository id>`. If the job fails with "Not authorized to perform sts:AssumeRoleWithWebIdentity", the failed event in CloudTrail (event name `AssumeRoleWithWebIdentity`, us-east-2) shows the subject GitHub sent as `userIdentity.userName`; the trust policy must match it exactly. Its SES test mails the SES mailbox simulator, so no real email is sent.

The job reads repository variables (Settings > Secrets and variables > Actions > Variables), none of them secret. Enter them once, and again after a rebuild (the ARNs and queue URL keep their names, but the OIDC provider and role are recreated):

```bash
terraform -chdir=infra output ci_repository_variables
```

Without them (e.g. after a teardown) the job skips its steps with a notice instead of failing. Commit flow: push the task branch, wait for CI to pass on it, then fast-forward `main` and push it; the real-AWS job then runs on `main`.

## Tearing down

```bash
terraform -chdir=infra destroy -var-file=infra/dev.tfvars \
  -var api_image=$(terraform -chdir=infra output -raw api_image) -var migrate_image=$(terraform -chdir=infra output -raw api_image)
```

This deletes the database with no final snapshot (no deletion protection, by design for dev) and the secrets at once (no recovery window), so a rebuild can reuse the names. The bootstrap stack (state bucket, ECR repository) is left in place and costs cents; destroy it only when giving up the environment for good (`terraform -chdir=infra/bootstrap destroy`, after emptying the bucket and the repository). A rebuild after a destroy starts with an empty database: run the first-deployment steps again: the bootstrap admin, and the SES sender and SNS subscription confirmations (both are Terraform resources, so a destroy deletes them and AWS sends new links).
