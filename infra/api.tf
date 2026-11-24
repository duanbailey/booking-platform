# The API on ECS Fargate (ADR 0008) behind an internal load balancer, plus the one-off `db migrate` task (T27a).
# Settings follow plan/tasks.md (T27a): connection string without credentials, the user and password injected
# separately from Secrets Manager, HTTPS enforced by CloudFront (Https__Redirect=false), and the VPC trusted as the
# proxy network across two hops (CloudFront and the load balancer; the real chain is checked in T27c).

resource "aws_ecs_cluster" "main" {
  name = local.name
}

resource "aws_cloudwatch_log_group" "api" {
  name              = "/${var.project}/${var.environment}/api"
  retention_in_days = 30
}

# ------------------------------------------------------------------------------------------------------ IAM

data "aws_iam_policy_document" "ecs_tasks_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

# Used by ECS itself: pull the image, write logs, read the secrets it injects.
resource "aws_iam_role" "api_execution" {
  name               = "${local.name}-api-execution"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_assume.json
}

resource "aws_iam_role_policy_attachment" "api_execution" {
  role       = aws_iam_role.api_execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

resource "aws_iam_role_policy" "api_execution_secrets" {
  name = "read-injected-secrets"
  role = aws_iam_role.api_execution.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect   = "Allow"
      Action   = ["secretsmanager:GetSecretValue"]
      Resource = [aws_secretsmanager_secret.jwt_signing_key.arn, aws_secretsmanager_secret.db_api.arn]
    }]
  })
}

# The migrate task's own execution role: only it may read the master password and the two users' passwords.
resource "aws_iam_role" "migrate_execution" {
  name               = "${local.name}-migrate-execution"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_assume.json
}

resource "aws_iam_role_policy_attachment" "migrate_execution" {
  role       = aws_iam_role.migrate_execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

resource "aws_iam_role_policy" "migrate_execution_secrets" {
  name = "read-injected-secrets"
  role = aws_iam_role.migrate_execution.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect   = "Allow"
      Action   = ["secretsmanager:GetSecretValue"]
      Resource = [local.db_master_secret_arn, aws_secretsmanager_secret.db_api.arn, aws_secretsmanager_secret.db_worker.arn]
    }]
  })
}

# Used by the API's code: the outbox relay publishes to the events queue (T21).
resource "aws_iam_role" "api_task" {
  name               = "${local.name}-api-task"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_assume.json
}

resource "aws_iam_role_policy" "api_task_sqs" {
  name = "publish-events"
  role = aws_iam_role.api_task.id
  policy = jsonencode({
    Version   = "2012-10-17"
    Statement = [{ Effect = "Allow", Action = ["sqs:SendMessage"], Resource = aws_sqs_queue.events.arn }]
  })
}

# ------------------------------------------------------------------------------------------- Task definitions

locals {
  connection_string = "Server=${local.db_host};Port=3306;Database=${local.db_name};"
  log_options = {
    awslogs-group         = aws_cloudwatch_log_group.api.name
    awslogs-region        = var.region
    awslogs-stream-prefix = "api"
  }
}

resource "aws_ecs_task_definition" "api" {
  family                   = "${local.name}-api"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.api_execution.arn
  task_role_arn            = aws_iam_role.api_task.arn
  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
  container_definitions = jsonencode([{
    name         = "api"
    image        = var.api_image
    essential    = true
    portMappings = [{ containerPort = 8080, protocol = "tcp" }]
    environment = [
      { name = "ASPNETCORE_ENVIRONMENT", value = "Production" },
      { name = "ConnectionStrings__BookingDb", value = local.connection_string },
      { name = "Database__User", value = "booking_api" },
      { name = "Outbox__QueueUrl", value = aws_sqs_queue.events.url },
      { name = "AWS_REGION", value = var.region },
      { name = "Https__Redirect", value = "false" },
      { name = "ForwardedHeaders__KnownNetworks__0", value = var.vpc_cidr },
      { name = "ForwardedHeaders__ForwardLimit", value = "2" },
    ]
    secrets = [
      { name = "Database__Password", valueFrom = aws_secretsmanager_secret.db_api.arn },
      { name = "Jwt__SigningKey", valueFrom = aws_secretsmanager_secret.jwt_signing_key.arn },
    ]
    logConfiguration = { logDriver = "awslogs", options = local.log_options }
  }])
}

# `db migrate` as the RDS master user: migrations, then the booking_api and booking_worker users (T27a).
resource "aws_ecs_task_definition" "migrate" {
  family                   = "${local.name}-migrate"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.migrate_execution.arn
  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
  container_definitions = jsonencode([{
    name      = "migrate"
    image     = var.migrate_image
    essential = true
    command   = ["db", "migrate"]
    environment = [
      { name = "ConnectionStrings__BookingDb", value = local.connection_string },
      { name = "Db__ApiUser", value = "booking_api" },
      { name = "Db__WorkerUser", value = "booking_worker" },
    ]
    secrets = [
      { name = "Database__User", valueFrom = "${local.db_master_secret_arn}:username::" },
      { name = "Database__Password", valueFrom = "${local.db_master_secret_arn}:password::" },
      { name = "Db__ApiPassword", valueFrom = aws_secretsmanager_secret.db_api.arn },
      { name = "Db__WorkerPassword", valueFrom = aws_secretsmanager_secret.db_worker.arn },
    ]
    logConfiguration = { logDriver = "awslogs", options = merge(local.log_options, { awslogs-stream-prefix = "migrate" }) }
  }])
}

# --------------------------------------------------------------------------------------- Load balancer, service

resource "aws_lb" "api" {
  name               = "${local.name}-api"
  internal           = true
  load_balancer_type = "application"
  security_groups    = [aws_security_group.alb.id]
  subnets            = aws_subnet.private[*].id
}

resource "aws_lb_target_group" "api" {
  name        = "${local.name}-api"
  port        = 8080
  protocol    = "HTTP"
  target_type = "ip"
  vpc_id      = aws_vpc.main.id
  health_check {
    path                = "/health" # shallow (T27a): a database outage doesn't replace every task
    matcher             = "200"
    interval            = 15
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
  deregistration_delay = 30
}

resource "aws_lb_listener" "http" {
  load_balancer_arn = aws_lb.api.arn
  port              = 80
  protocol          = "HTTP" # CloudFront terminates HTTPS; this hop stays inside AWS (VPC origin)
  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.api.arn
  }
}

resource "aws_ecs_service" "api" {
  name            = "${local.name}-api"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.api.arn
  desired_count   = var.api_desired_count
  launch_type     = "FARGATE"

  network_configuration {
    subnets          = aws_subnet.private[*].id
    security_groups  = [aws_security_group.api.id]
    assign_public_ip = false
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.api.arn
    container_name   = "api"
    container_port   = 8080
  }

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  depends_on = [aws_lb_listener.http]
}
