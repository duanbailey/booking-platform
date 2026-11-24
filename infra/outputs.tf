output "api_url" {
  description = "The API's public HTTPS address (CloudFront)."
  value       = local.api_url
}

output "api_image" {
  description = "The image the API service runs; the deploy script reads it to migrate before rolling out."
  value       = var.api_image
}

output "cluster" {
  value = aws_ecs_cluster.main.name
}

output "api_service" {
  value = aws_ecs_service.api.name
}

output "migrate_task_definition" {
  value = aws_ecs_task_definition.migrate.arn
}

output "private_subnets" {
  description = "Where the deploy script runs the migrate task."
  value       = aws_subnet.private[*].id
}

output "api_security_group" {
  value = aws_security_group.api.id
}

output "api_log_group" {
  value = aws_cloudwatch_log_group.api.name
}

output "events_queue_url" {
  description = "For the real-AWS SQS test (BOOKING_TEST_SQS_QUEUE_URL)."
  value       = aws_sqs_queue.events.url
}

output "reminder_settings" {
  description = "For the real-AWS Scheduler test (BOOKING_TEST_SCHEDULER_*)."
  value = {
    group      = aws_scheduler_schedule_group.reminders.name
    target_arn = aws_sqs_queue.events.arn
    role_arn   = aws_iam_role.scheduler.arn
    dlq_arn    = aws_sqs_queue.reminders_dlq.arn
  }
}

output "alarm_topic" {
  value = aws_sns_topic.alarms.arn
}
