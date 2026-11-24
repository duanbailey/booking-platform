# The notification worker (T23-T26): a Node Lambda in the private subnets, fed by the events queue with partial batch
# failures (ReportBatchItemFailures). It connects to MySQL as booking_worker (privileges from worker/db/worker-grants.sql,
# set by db migrate), reading the password from Secrets Manager at startup (T27b), sends email through SES and
# manages reminder schedules in EventBridge Scheduler, which delivers ReminderDue back through the events queue (T25).

locals {
  worker_timeout_seconds = 60
}

data "archive_file" "worker" {
  type        = "zip"
  source_dir  = var.worker_package_dir
  output_path = "${path.module}/.build/worker.zip"
}

resource "aws_cloudwatch_log_group" "worker" {
  name              = "/aws/lambda/${local.name}-worker"
  retention_in_days = 30
}

resource "aws_iam_role" "worker" {
  name = "${local.name}-worker"
  assume_role_policy = jsonencode({
    Version   = "2012-10-17"
    Statement = [{ Effect = "Allow", Action = "sts:AssumeRole", Principal = { Service = "lambda.amazonaws.com" } }]
  })
}

resource "aws_iam_role_policy_attachment" "worker_vpc" {
  role       = aws_iam_role.worker.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaVPCAccessExecutionRole" # ENIs and logs
}

resource "aws_iam_role_policy" "worker" {
  name = "worker"
  role = aws_iam_role.worker.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "ConsumeEvents"
        Effect   = "Allow"
        Action   = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:GetQueueAttributes", "sqs:ChangeMessageVisibility"]
        Resource = aws_sqs_queue.events.arn
      },
      {
        Sid      = "DatabasePassword"
        Effect   = "Allow"
        Action   = ["secretsmanager:GetSecretValue"]
        Resource = aws_secretsmanager_secret.db_worker.arn
      },
      {
        Sid      = "SendEmail"
        Effect   = "Allow"
        Action   = ["ses:SendEmail"]
        Resource = aws_sesv2_email_identity.sender.arn
      },
      {
        Sid      = "ManageReminders"
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

resource "aws_lambda_function" "worker" {
  function_name    = "${local.name}-worker"
  role             = aws_iam_role.worker.arn
  runtime          = "nodejs22.x"
  handler          = "index.handler"
  filename         = data.archive_file.worker.output_path
  source_code_hash = data.archive_file.worker.output_base64sha256
  timeout          = local.worker_timeout_seconds
  memory_size      = 512
  architectures    = ["x86_64"]

  vpc_config {
    subnet_ids         = aws_subnet.private[*].id
    security_group_ids = [aws_security_group.worker.id]
  }

  environment {
    variables = {
      DB_HOST                = local.db_host
      DB_PORT                = "3306"
      DB_USER                = "booking_worker"
      DB_NAME                = local.db_name
      DB_PASSWORD_SECRET_ARN = aws_secretsmanager_secret.db_worker.arn
      EMAIL_FROM             = var.email_from
      REMINDER_GROUP         = aws_scheduler_schedule_group.reminders.name
      REMINDER_TARGET_ARN    = aws_sqs_queue.events.arn
      REMINDER_ROLE_ARN      = aws_iam_role.scheduler.arn
      REMINDER_DLQ_ARN       = aws_sqs_queue.reminders_dlq.arn
      APP_BASE_URL           = local.app_base_url
      APP_NAME               = var.app_name
    }
  }

  depends_on = [aws_cloudwatch_log_group.worker, aws_iam_role_policy_attachment.worker_vpc]
}

resource "aws_lambda_event_source_mapping" "worker" {
  event_source_arn        = aws_sqs_queue.events.arn
  function_name           = aws_lambda_function.worker.arn
  batch_size              = 10
  function_response_types = ["ReportBatchItemFailures"] # T23: the first failed message and every later one
}

# ----------------------------------------------------------------------------------------------------- SES

# One verified sender address (decided 2026-11-24): AWS emails it a verification link on the first apply. In the SES
# sandbox, mail is delivered only to verified addresses until production access is granted.
resource "aws_sesv2_email_identity" "sender" {
  email_identity = var.email_from
}

# --------------------------------------------------------------------------------------- EventBridge Scheduler

resource "aws_scheduler_schedule_group" "reminders" {
  name = "${local.name}-reminders"
}

# The role Scheduler assumes to deliver ReminderDue to the events queue, and undeliverable ones to the reminders DLQ.
resource "aws_iam_role" "scheduler" {
  name = "${local.name}-scheduler"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Action    = "sts:AssumeRole"
      Principal = { Service = "scheduler.amazonaws.com" }
      Condition = { StringEquals = { "aws:SourceAccount" = data.aws_caller_identity.current.account_id } }
    }]
  })
}

resource "aws_iam_role_policy" "scheduler" {
  name = "deliver-reminders"
  role = aws_iam_role.scheduler.id
  policy = jsonencode({
    Version   = "2012-10-17"
    Statement = [{ Effect = "Allow", Action = ["sqs:SendMessage"], Resource = [aws_sqs_queue.events.arn, aws_sqs_queue.reminders_dlq.arn] }]
  })
}
