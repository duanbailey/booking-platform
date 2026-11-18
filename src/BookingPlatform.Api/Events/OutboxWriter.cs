using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Observability;

namespace BookingPlatform.Api.Events;

/// <summary>
/// Transactional outbox writer (specs/06-events.md "Delivery"). <see cref="Add"/> only
/// stages the row in the caller's <see cref="BookingDbContext"/>; the caller's own
/// SaveChanges writes it, inside the caller's transaction, so the event exists if and
/// only if that transaction commits. A relay publishes rows after commit (ADR 0008).
/// </summary>
public sealed class OutboxWriter(BookingDbContext db, TimeProvider time, IHttpContextAccessor http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Required nullable fields (e.g. AccessGranted.inviteId) are written as null.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(), new UtcDateTimeConverter() },
    };

    /// <summary>
    /// Stages <paramref name="domainEvent"/> in the outbox. The correlation ID is
    /// <paramref name="correlationId"/> if given, else the current request's
    /// X-Correlation-Id, else a new ID (e.g. for an operator script).
    /// </summary>
    public OutboxMessage Add(DomainEvent domainEvent, string? correlationId = null)
    {
        // An explicit ID follows the same rule as a request's X-Correlation-Id (specs/07-nfr.md).
        if (correlationId is not null && !CorrelationIdMiddleware.IsValidId(correlationId))
        {
            throw new ArgumentException(
                "A correlation ID must be 1-64 characters of A-Z a-z 0-9 - _ .", nameof(correlationId));
        }

        // occurredAt is written with millisecond precision; created_at stores the same instant.
        var now = time.GetUtcNow();
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
        var eventId = Guid.CreateVersion7(now);

        var envelope = new EventEnvelope(
            eventId,
            domainEvent.EventType,
            domainEvent.SchemaVersion,
            now.UtcDateTime,
            correlationId ?? http.HttpContext?.GetCorrelationId() ?? Guid.NewGuid().ToString("N"),
            domainEvent);

        var message = new OutboxMessage
        {
            Id = eventId,
            EventType = domainEvent.EventType,
            SchemaVersion = domainEvent.SchemaVersion,
            MessageGroupId = domainEvent.MessageGroupId.ToString(),
            Payload = JsonSerializer.Serialize(envelope, Json),
            CreatedAt = now.UtcDateTime,
        };
        db.Outbox.Add(message);
        return message;
    }

    /// <summary>The envelope (specs/events/envelope.v1.schema.json). Data is serialized by its runtime type.</summary>
    private sealed record EventEnvelope(
        Guid EventId, string EventType, int SchemaVersion, DateTime OccurredAt, string CorrelationId, object Data);

    /// <summary>R2: event times are UTC with a Z suffix; anything else is a caller bug.</summary>
    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("The outbox writer only writes events.");

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new InvalidOperationException($"Event times must be UTC (R2), got a {value.Kind} DateTime.");
            }
            writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        }
    }
}
