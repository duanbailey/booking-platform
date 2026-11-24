# Alarms (T27b, decided 2026-11-24), all to one SNS topic with an optional email subscription:
#   - either dead-letter queue is not empty (specs/06-events.md "Failures"; reminders Scheduler gave up on)
#   - the oldest unpublished outbox row is older than 5 minutes, or the metric is missing (relay or database down;
#     the API writes it as an EMF line, T27a)
#   - the outbox relay logs errors
#   - the worker Lambda reports errors (a failed start, a crash; failed messages are retried and end in the DLQ)

resource "aws_sns_topic" "alarms" {
  name = "${local.name}-alarms"
}

resource "aws_sns_topic_subscription" "alarm_email" {
  count     = var.alarm_email == "" ? 0 : 1
  topic_arn = aws_sns_topic.alarms.arn
  protocol  = "email"
  endpoint  = var.alarm_email
}

resource "aws_cloudwatch_metric_alarm" "dlq_not_empty" {
  for_each = {
    events    = aws_sqs_queue.events_dlq.name
    reminders = aws_sqs_queue.reminders_dlq.name
  }
  alarm_name          = "${local.name}-${each.key}-dlq-not-empty"
  alarm_description   = "Messages in the ${each.key} dead-letter queue: investigate, then redrive or delete them."
  namespace           = "AWS/SQS"
  metric_name         = "ApproximateNumberOfMessagesVisible"
  dimensions          = { QueueName = each.value }
  statistic           = "Maximum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 0
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
  alarm_actions       = [aws_sns_topic.alarms.arn]
  ok_actions          = [aws_sns_topic.alarms.arn]
}

resource "aws_cloudwatch_metric_alarm" "outbox_age" {
  alarm_name          = "${local.name}-outbox-stuck"
  alarm_description   = "The oldest unpublished outbox event is over 5 minutes old, or the API stopped reporting it (relay or database down)."
  namespace           = "BookingPlatform"
  metric_name         = "OutboxOldestUnpublishedAgeSeconds"
  statistic           = "Maximum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 300
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "breaching"
  alarm_actions       = [aws_sns_topic.alarms.arn]
  ok_actions          = [aws_sns_topic.alarms.arn]
}

# The API's JSON log lines carry LogLevel and Category (T3); the relay's failures are Error lines from these categories.
resource "aws_cloudwatch_log_metric_filter" "relay_errors" {
  name           = "${local.name}-relay-errors"
  log_group_name = aws_cloudwatch_log_group.api.name
  pattern        = "{ ($.LogLevel = \"Error\") && (($.Category = \"BookingPlatform.Api.Outbox.OutboxRelay\") || ($.Category = \"BookingPlatform.Api.Outbox.OutboxRelayService\")) }"
  metric_transformation {
    name          = "OutboxRelayErrors"
    namespace     = "BookingPlatform"
    value         = "1"
    default_value = "0"
  }
}

resource "aws_cloudwatch_metric_alarm" "relay_errors" {
  alarm_name          = "${local.name}-relay-errors"
  alarm_description   = "The outbox relay logged errors (failed publishes or rounds)."
  namespace           = "BookingPlatform"
  metric_name         = "OutboxRelayErrors"
  statistic           = "Sum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 0
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
  alarm_actions       = [aws_sns_topic.alarms.arn]
  ok_actions          = [aws_sns_topic.alarms.arn]
}

resource "aws_cloudwatch_metric_alarm" "worker_errors" {
  alarm_name          = "${local.name}-worker-errors"
  alarm_description   = "The worker Lambda failed (e.g. could not start, or crashed)."
  namespace           = "AWS/Lambda"
  metric_name         = "Errors"
  dimensions          = { FunctionName = aws_lambda_function.worker.function_name }
  statistic           = "Sum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 0
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
  alarm_actions       = [aws_sns_topic.alarms.arn]
  ok_actions          = [aws_sns_topic.alarms.arn]
}
