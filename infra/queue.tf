# Queues (specs/06-events.md "Delivery"). The events queue is FIFO with the booking ID (or user ID) as message group;
# the relay sets eventId as the deduplication ID, and EventBridge Scheduler, which cannot set one, relies on
# content-based deduplication (T25). After 5 failed receives a message goes to the FIFO dead-letter queue (alarmed).
# Reminders Scheduler cannot deliver within an hour go to their own dead-letter queue, which Scheduler requires to be
# a standard queue (T27b).

resource "aws_sqs_queue" "events_dlq" {
  name                      = "${local.name}-events-dlq.fifo"
  fifo_queue                = true
  message_retention_seconds = 1209600 # 14 days, the maximum, to give time to investigate
}

resource "aws_sqs_queue" "events" {
  name                        = "${local.name}-events.fifo"
  fifo_queue                  = true
  content_based_deduplication = true
  # At least six times the worker's timeout, as AWS recommends for a Lambda event source.
  visibility_timeout_seconds = 6 * local.worker_timeout_seconds
  message_retention_seconds  = 345600 # 4 days
  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.events_dlq.arn
    maxReceiveCount     = 5
  })
}

resource "aws_sqs_queue" "reminders_dlq" {
  name                      = "${local.name}-reminders-dlq"
  message_retention_seconds = 1209600
}
