using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Options;

namespace BookingPlatform.Api.Outbox;

/// <summary>
/// Publishes to the SQS FIFO queue with SendMessageBatch (T21): the body is the stored envelope as MySQL returns it
/// from the JSON column (the same content as written, but MySQL's canonical form: keys sorted, its own spacing, so
/// consumers must not depend on key order or bytes), the message group is the outbox row's, and the deduplication
/// ID is the eventId, so a republished row is dropped by SQS within its five-minute window (ADR 0008). No message attributes. Credentials and region come from the
/// default AWS chain (the ECS task role in production). Checked against AWS by the tagged real-AWS test (ADR 0007).
/// </summary>
public sealed class SqsMessagePublisher(IAmazonSQS sqs, IOptions<OutboxOptions> options) : IMessagePublisher
{
    public async Task<IReadOnlyList<PublishOutcome>> PublishAsync(IReadOnlyList<OutgoingMessage> batch, CancellationToken ct)
    {
        var response = await sqs.SendMessageBatchAsync(BuildRequest(options.Value.QueueUrl!, batch), ct);
        return MapResponse(batch, response);
    }

    /// <summary>Batch entry IDs are the eventIds without hyphens (SQS allows letters, digits, - and _, up to 80).</summary>
    public static SendMessageBatchRequest BuildRequest(string queueUrl, IReadOnlyList<OutgoingMessage> batch) => new()
    {
        QueueUrl = queueUrl,
        Entries = batch.Select(m => new SendMessageBatchRequestEntry
        {
            Id = EntryId(m),
            MessageBody = m.Body,
            MessageGroupId = m.MessageGroupId,
            MessageDeduplicationId = m.EventId.ToString(),
        }).ToList(),
    };

    /// <summary>One outcome per message; SDK v4 leaves an empty list null.</summary>
    public static IReadOnlyList<PublishOutcome> MapResponse(IReadOnlyList<OutgoingMessage> batch, SendMessageBatchResponse response)
    {
        var accepted = (response.Successful ?? []).Select(s => s.Id).ToHashSet();
        var failed = (response.Failed ?? []).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => $"{g.First().Code}: {g.First().Message}");
        return batch.Select(m =>
        {
            var id = EntryId(m);
            var error = accepted.Contains(id) ? null : failed.GetValueOrDefault(id) ?? "SQS returned no result for this message.";
            return new PublishOutcome(m.EventId, error);
        }).ToList();
    }

    private static string EntryId(OutgoingMessage m) => m.EventId.ToString("N");
}
