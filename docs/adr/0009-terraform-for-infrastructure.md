# ADR 0009: Use Terraform for infrastructure as code

- **Status:** Accepted
- **Date:** 2026-11-16
- **Related specs:** [specs/08-architecture.md](../../specs/08-architecture.md)
- **Related ADRs:** [ADR 0007](0007-aws-testing-with-fakes.md), [ADR 0008](0008-outbox-relay-in-api.md)

## Context

specs/08-architecture.md requires infrastructure as code, partly because it is a skill employers look for, and left the tool open (CDK or Terraform). T27 provisions the API on ECS Fargate (ADR 0008) behind a load balancer, RDS MySQL, the SQS FIFO queue and its FIFO dead-letter queue with an alarm, the Node worker Lambda, SES, EventBridge Scheduler, Secrets Manager, CloudWatch and IAM, plus the dev-account resources for the real-AWS test suite (ADR 0007).

## Options considered

1. **Terraform** (chosen). The most widely recognised infrastructure-as-code tool, not tied to AWS; `terraform plan` shows exact changes before they are applied.
2. **OpenTofu.** The open-source fork of Terraform, same language and workflow. Not chosen: Terraform's licence (BSL 1.1 since version 1.6, 2023) only restricts building products that compete with HashiCorp, which does not apply here.
3. **AWS CDK (TypeScript or C#).** The least code for this stack, with high-level constructs and generated least-privilege grants, and no state file to host. Not chosen: AWS-only, deploys through CloudFormation (slow, and failed rollbacks can leave a stack stuck), and its constructs hide the resources they create.

## Decision

Infrastructure is defined in Terraform, under `infra/`. State is kept in an S3 backend with locking. The bucket is created once, outside the main configuration.

## Consequences

- More to write than CDK: the network, load balancer, IAM policies and Lambda packaging are spelled out, or taken from well-known community modules.
- IAM policies are written explicitly and kept to least privilege; they are reviewed like code.
- The state bucket must exist before the first `terraform apply`, and its access is restricted because state can hold secrets.
- The worker's limited MySQL user (specs/07-nfr.md) is not created by Terraform; it needs a script or migration run from inside the private network (T26, T27).
- If the licence ever becomes a concern, OpenTofu can run the same configuration with little change.
