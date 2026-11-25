# CI access (T27d, decided 2026-11-25): GitHub Actions assumes this role through OIDC to run the real-AWS tests
# (ADR 0007) against this environment. Only pushes to main (and manual runs on main) of var.github_repository may
# assume it, and it may do only what those tests do. Created only when var.github_repository is set; destroyed with
# the environment. The values the workflow reads are in the ci_repository_variables output (docs/runbook.md, CI).
# GitHub's subject claim carries the owner's and repository's immutable IDs (repo:owner@id/name@id:ref:...), so a
# deleted repository whose name is reused cannot assume the role; the first run failed against the plain owner/name.

locals {
  ci_enabled = var.github_repository != ""
}

resource "aws_iam_openid_connect_provider" "github" {
  count          = local.ci_enabled ? 1 : 0
  url            = "https://token.actions.githubusercontent.com"
  client_id_list = ["sts.amazonaws.com"]
}

resource "aws_iam_role" "ci" {
  count = local.ci_enabled ? 1 : 0
  name  = "${local.name}-ci"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Action    = "sts:AssumeRoleWithWebIdentity"
      Principal = { Federated = aws_iam_openid_connect_provider.github[0].arn }
      Condition = {
        StringEquals = {
          "token.actions.githubusercontent.com:aud" = "sts.amazonaws.com"
          "token.actions.githubusercontent.com:sub" = "repo:${var.github_repository}:ref:refs/heads/main"
        }
      }
    }]
  })
}

resource "aws_iam_role_policy" "ci_real_aws_tests" {
  count = local.ci_enabled ? 1 : 0
  name  = "real-aws-tests"
  role  = aws_iam_role.ci[0].id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "SqsTestQueue"
        Effect   = "Allow"
        Action   = ["sqs:SendMessage", "sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility"]
        Resource = aws_sqs_queue.sqs_test.arn
      },
      {
        Sid      = "SendEmail"
        Effect   = "Allow"
        Action   = ["ses:SendEmail"]
        Resource = aws_sesv2_email_identity.sender.arn
      },
      {
        Sid      = "ManageTestSchedules"
        Effect   = "Allow"
        Action   = ["scheduler:CreateSchedule", "scheduler:UpdateSchedule", "scheduler:DeleteSchedule"]
        Resource = "arn:aws:scheduler:${var.region}:${data.aws_caller_identity.current.account_id}:schedule/${aws_scheduler_schedule_group.reminders.name}/*"
      },
      {
        Sid      = "HandScheduleRoleToScheduler"
        Effect   = "Allow"
        Action   = ["iam:PassRole"]
        Resource = aws_iam_role.scheduler.arn
      },
    ]
  })
}

output "ci_repository_variables" {
  description = "GitHub repository variables (Settings > Secrets and variables > Actions > Variables) for the real-AWS job. None are secrets."
  value = local.ci_enabled ? {
    AWS_CI_ROLE_ARN                   = aws_iam_role.ci[0].arn
    BOOKING_TEST_SQS_QUEUE_URL        = aws_sqs_queue.sqs_test.url
    BOOKING_TEST_SES_FROM             = var.email_from
    BOOKING_TEST_SCHEDULER_GROUP      = aws_scheduler_schedule_group.reminders.name
    BOOKING_TEST_SCHEDULER_TARGET_ARN = aws_sqs_queue.events.arn
    BOOKING_TEST_SCHEDULER_ROLE_ARN   = aws_iam_role.scheduler.arn
    BOOKING_TEST_SCHEDULER_DLQ_ARN    = aws_sqs_queue.reminders_dlq.arn
  } : null
}
