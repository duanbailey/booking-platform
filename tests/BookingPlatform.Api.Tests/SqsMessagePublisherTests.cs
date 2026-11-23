using Amazon.SQS.Model;
using BookingPlatform.Api.Outbox;

namespace BookingPlatform.Api.Tests;

/// <summary>
/// T21: how outbox rows become an SQS SendMessageBatch request and how its response becomes outcomes, without AWS
/// (the call itself is checked by the tagged real-AWS test, ADR 0007). Decided 2026-11-23: the body is the stored
/// envelope as read from the database (MySQL's canonical JSON), MessageGroupId is the row's group,
/// MessageDeduplicationId is the eventId, no attributes.
/// </summary>
public class SqsMessagePublisherTests
{
    private static readonly OutgoingMessage A = new(Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b"), "booking-a", """{"eventId":"a"}""");
    private static readonly OutgoingMessage B = new(Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c"), "user-b", """{"eventId":"b"}""");

    [Fact]
    public void The_request_carries_body_group_and_eventId_as_deduplication_ID()
    {
        var request = SqsMessagePublisher.BuildRequest("https://sqs.eu-west-1.amazonaws.com/1/q.fifo", [A, B]);

        Assert.Equal("https://sqs.eu-west-1.amazonaws.com/1/q.fifo", request.QueueUrl);
        Assert.Equal(2, request.Entries.Count);
        var entry = request.Entries[0];
        Assert.Equal("0193a1b2c3d47e5f8a9b0c1d2e3f4a5b", entry.Id);
        Assert.Equal("""{"eventId":"a"}""", entry.MessageBody);
        Assert.Equal("booking-a", entry.MessageGroupId);
        Assert.Equal("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", entry.MessageDeduplicationId);
        Assert.True(entry.MessageAttributes is null || entry.MessageAttributes.Count == 0);
        Assert.Null(entry.DelaySeconds);
    }

    [Fact]
    public void Accepted_and_failed_entries_map_to_outcomes_by_eventId()
    {
        var response = new SendMessageBatchResponse
        {
            Successful = [new SendMessageBatchResultEntry { Id = "0193a1b2c3d47e5f8a9b0c1d2e3f4a5c" }],
            Failed = [new BatchResultErrorEntry { Id = "0193a1b2c3d47e5f8a9b0c1d2e3f4a5b", Code = "InvalidParameterValue", Message = "Too big" }],
        };

        var outcomes = SqsMessagePublisher.MapResponse([A, B], response);

        Assert.Equal([new PublishOutcome(A.EventId, "InvalidParameterValue: Too big"), new PublishOutcome(B.EventId, null)], outcomes);
    }

    [Fact]
    public void An_entry_missing_from_the_response_counts_as_failed_and_null_lists_are_empty()
    {
        var outcomes = SqsMessagePublisher.MapResponse([A], new SendMessageBatchResponse { Successful = null, Failed = null });

        Assert.Equal([new PublishOutcome(A.EventId, "SQS returned no result for this message.")], outcomes);
    }

    [Fact]
    public void The_SQS_client_gives_up_quickly_so_a_hung_call_becomes_an_ordinary_failure()
    {
        // T21 review (2026-11-23): the relay holds its rows' locks while sending, so a hang must not last minutes.
        var config = OutboxRelayRegistration.SqsConfig();

        Assert.Equal(TimeSpan.FromSeconds(10), config.Timeout);
        Assert.Equal(2, config.MaxErrorRetry);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 32)]
    [InlineData(7, 60)]
    [InlineData(1000, 60)]
    public void The_retry_delay_doubles_from_one_second_up_to_sixty(int attempt, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), OutboxRelay.RetryDelay(attempt));
    }
}
