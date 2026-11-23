using Amazon.SQS;
using Amazon.SQS.Model;
using BookingPlatform.Api.Outbox;
using Microsoft.Extensions.Options;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// A test against a real SQS FIFO queue (ADR 0007). Skipped unless BOOKING_TEST_SQS_QUEUE_URL is set (decided
/// 2026-11-23), so ordinary runs never need AWS; credentials and region come from the default AWS chain (e.g.
/// AWS_PROFILE and AWS_REGION). Run only these with: dotnet test --filter Category=RealAws.
/// </summary>
public sealed class RealAwsFactAttribute : FactAttribute
{
    public const string QueueUrlVariable = "BOOKING_TEST_SQS_QUEUE_URL";

    public RealAwsFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(QueueUrlVariable)))
            Skip = $"Real-AWS test: set {QueueUrlVariable} to a dev SQS FIFO queue URL to run it.";
    }
}

[Trait("Category", "RealAws")]
public class RealAwsSqsTests
{
    [RealAwsFact]
    public async Task The_SQS_publisher_sends_to_a_FIFO_queue_with_group_and_deduplication_ID()
    {
        var queueUrl = Environment.GetEnvironmentVariable(RealAwsFactAttribute.QueueUrlVariable)!;
        using var sqs = new AmazonSQSClient();
        var publisher = new SqsMessagePublisher(sqs, Options.Create(new OutboxOptions { QueueUrl = queueUrl }));
        var run = Guid.NewGuid().ToString("N");
        var first = new OutgoingMessage(Guid.NewGuid(), $"test-{run}-a", $$"""{"run":"{{run}}","n":1}""");
        var second = new OutgoingMessage(Guid.NewGuid(), $"test-{run}-b", $$"""{"run":"{{run}}","n":2}""");

        var outcomes = await publisher.PublishAsync([first, second], CancellationToken.None);
        Assert.All(outcomes, o => Assert.Null(o.Error));
        // A republished row (same eventId) is accepted and dropped by SQS deduplication (ADR 0008).
        Assert.All(await publisher.PublishAsync([first], CancellationToken.None), o => Assert.Null(o.Error));

        // Only this run's messages are deleted (T21 review): anything else on a shared dev queue is handed back at
        // once (visibility timeout 0) for its real consumer.
        var received = new List<Message>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var quietAfterBoth = false;
        while (DateTime.UtcNow < deadline)
        {
            var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5,
                MessageSystemAttributeNames = ["MessageGroupId", "MessageDeduplicationId"],
            });
            var mine = 0;
            foreach (var message in response.Messages ?? [])
            {
                if (message.Body.Contains(run))
                {
                    received.Add(message);
                    mine++;
                    await sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle);
                }
                else
                {
                    await sqs.ChangeMessageVisibilityAsync(queueUrl, message.ReceiptHandle, 0);
                }
            }
            // Once both arrived, one more quiet receive (5 s long poll) gives a duplicate the chance to show up.
            if (received.Count >= 2 && mine == 0)
            {
                if (quietAfterBoth) break;
                quietAfterBoth = true;
            }
        }

        Assert.Equal(2, received.Count);
        var a = Assert.Single(received, m => m.Body == first.Body);
        Assert.Equal(first.MessageGroupId, a.Attributes["MessageGroupId"]);
        Assert.Equal(first.EventId.ToString(), a.Attributes["MessageDeduplicationId"]);
        Assert.Single(received, m => m.Body == second.Body);
    }
}
