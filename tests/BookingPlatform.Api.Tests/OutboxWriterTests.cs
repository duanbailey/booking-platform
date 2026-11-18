using System.Text.Json;
using System.Text.Json.Nodes;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Events;
using BookingPlatform.Api.Observability;
using BookingPlatform.Api.Tests.Infrastructure;
using BookingPlatform.Tests.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookingPlatform.Api.Tests;

/// <summary>
/// T5: the outbox writer builds the envelope (specs/06-events.md, specs/events/) and stages
/// a row in the caller's DbContext. Commit and rollback against MySQL are in the
/// integration tests (OutboxTests).
/// </summary>
public class OutboxWriterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 30, 15, 123, TimeSpan.Zero);

    // Add() never opens a connection, so no database is needed.
    private static BookingDbContext Context() => new(new DbContextOptionsBuilder<BookingDbContext>()
        .UseMySQL("Server=unused;Database=unused").Options);

    private static OutboxWriter Writer(BookingDbContext? db = null, HttpContext? request = null) =>
        new(db ?? Context(), new FixedTimeProvider(Now), new HttpContextAccessor { HttpContext = request });

    private static JsonObject Envelope(OutboxMessage message) => JsonNode.Parse(message.Payload)!.AsObject();

    [Fact]
    public void Every_API_event_type_has_a_schema_and_a_sample_and_only_ReminderDue_is_left_out()
    {
        var apiTypes = typeof(DomainEvent).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(DomainEvent)) && !t.IsAbstract)
            .Select(t => t.Name).Order().ToList();

        Assert.Equal(apiTypes, SampleEvents.All.Keys.Order());
        // ReminderDue is sent by EventBridge Scheduler, not the API (specs/06-events.md).
        Assert.Equal(apiTypes.Append("ReminderDue").Order(), EventSchemas.EventTypes);
    }

    [Theory]
    [MemberData(nameof(SampleEvents.Names), MemberType = typeof(SampleEvents))]
    public void Every_event_validates_against_its_JSON_schema(string eventType)
    {
        var message = Writer().Add(SampleEvents.All[eventType]);

        Assert.Equal(eventType, message.EventType);
        EventSchemas.AssertValid(message.Payload);
    }

    [Theory]
    [InlineData("missing required data field")]
    [InlineData("extra data field")]
    [InlineData("time without Z")]
    [InlineData("id that is not a uuid")]
    [InlineData("wrong eventType")]
    [InlineData("empty correlationId")]
    public void Schema_check_rejects_invalid_envelopes(string breakage)
    {
        // Guards against the schema check passing vacuously (e.g. refs not resolved).
        var envelope = Envelope(Writer().Add(SampleEvents.All["BookingCreated"]));
        var data = envelope["data"]!.AsObject();
        switch (breakage)
        {
            case "missing required data field": data.Remove("customerId"); break;
            case "extra data field": data["customerName"] = "Ann"; break;
            case "time without Z": data["slot"]!["startUtc"] = "2026-12-01T09:00:00+00:00"; break;
            case "id that is not a uuid": data["bookingId"] = "not-a-uuid"; break;
            case "wrong eventType": envelope["eventType"] = "BookingCancelled"; break;
            case "empty correlationId": envelope["correlationId"] = ""; break;
        }

        Assert.NotEmpty(EventSchemas.Errors(envelope.ToJsonString(), "BookingCreated.v1.schema.json"));
    }

    [Fact]
    public void Envelope_and_row_share_the_event_id_type_version_and_time()
    {
        var message = Writer().Add(SampleEvents.All["PasswordChanged"], correlationId: "corr-1");
        var envelope = Envelope(message);

        Assert.Equal(message.Id, Guid.Parse(envelope["eventId"]!.GetValue<string>()));
        Assert.Equal(7, message.Id.Version); // time-ordered
        Assert.Equal("PasswordChanged", envelope["eventType"]!.GetValue<string>());
        Assert.Equal(1, envelope["schemaVersion"]!.GetValue<int>());
        Assert.Equal(1, message.SchemaVersion);
        Assert.Equal("2026-11-18T09:30:15.123Z", envelope["occurredAt"]!.GetValue<string>());
        Assert.Equal(Now.UtcDateTime, message.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, message.CreatedAt.Kind);
        Assert.Null(message.PublishedAt);
        Assert.Equal("corr-1", envelope["correlationId"]!.GetValue<string>());
    }

    [Fact]
    public void Data_uses_camelCase_names_string_enums_and_UTC_times_with_Z()
    {
        var envelope = Envelope(Writer().Add(SampleEvents.All["BookingCancelled"]));
        var data = envelope["data"]!;

        Assert.Equal("TimeOff", data["reason"]!.GetValue<string>());
        Assert.Equal("Owner", data["cancelledBy"]!["role"]!.GetValue<string>());
        Assert.Equal("2026-12-01T09:00:00.000Z", data["slot"]!["startUtc"]!.GetValue<string>());
        Assert.Equal("Europe/London", data["businessTimeZone"]!.GetValue<string>());
    }

    [Fact]
    public void Null_fields_are_written_as_null_not_omitted()
    {
        // AccessGranted requires inviteId, null when the user already has a password.
        var data = Envelope(Writer().Add(SampleEvents.All["AccessGranted"]))["data"]!.AsObject();

        Assert.True(data.ContainsKey("inviteId"));
        Assert.Null(data["inviteId"]);
    }

    [Theory]
    [MemberData(nameof(SampleEvents.Names), MemberType = typeof(SampleEvents))]
    public void Message_group_follows_the_FIFO_rules(string eventType)
    {
        var message = Writer().Add(SampleEvents.All[eventType]);

        Assert.Equal(SampleEvents.ExpectedMessageGroup[eventType].ToString(), message.MessageGroupId);
    }

    [Fact]
    public void Add_only_stages_the_row_in_the_callers_context()
    {
        using var db = Context();
        var message = Writer(db).Add(SampleEvents.All["PasswordChanged"]);

        Assert.Equal(EntityState.Added, db.Entry(message).State);
    }

    [Fact]
    public void Each_event_gets_its_own_id()
    {
        var writer = Writer();
        var a = writer.Add(SampleEvents.All["PasswordChanged"]);
        var b = writer.Add(SampleEvents.All["PasswordChanged"]);

        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public async Task Correlation_id_comes_from_the_request()
    {
        var request = new DefaultHttpContext();
        request.Request.Headers[CorrelationIdMiddleware.HeaderName] = "request-42";
        OutboxMessage? message = null;
        var middleware = new CorrelationIdMiddleware(context =>
        {
            message = Writer(request: context).Add(SampleEvents.All["PasswordChanged"]);
            return Task.CompletedTask;
        }, NullLogger<CorrelationIdMiddleware>.Instance);

        await middleware.InvokeAsync(request);

        Assert.Equal("request-42", Envelope(message!)["correlationId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Explicit_correlation_id_wins_over_the_request()
    {
        var request = new DefaultHttpContext();
        request.Request.Headers[CorrelationIdMiddleware.HeaderName] = "request-42";
        OutboxMessage? message = null;
        var middleware = new CorrelationIdMiddleware(context =>
        {
            message = Writer(request: context).Add(SampleEvents.All["PasswordChanged"], correlationId: "script-7");
            return Task.CompletedTask;
        }, NullLogger<CorrelationIdMiddleware>.Instance);

        await middleware.InvokeAsync(request);

        Assert.Equal("script-7", Envelope(message!)["correlationId"]!.GetValue<string>());
    }

    [Fact]
    public void Outside_a_request_a_new_correlation_id_is_generated()
    {
        var writer = Writer(request: null);
        var a = Envelope(writer.Add(SampleEvents.All["PasswordChanged"]))["correlationId"]!.GetValue<string>();
        var b = Envelope(writer.Add(SampleEvents.All["PasswordChanged"]))["correlationId"]!.GetValue<string>();

        Assert.Matches("^[A-Za-z0-9._-]{1,64}$", a);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("trailing-newline\n")]
    [InlineData("x-65-characters-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public void Explicit_correlation_id_must_follow_the_request_id_rule(string correlationId)
    {
        // specs/07-nfr.md: 1-64 characters of A-Z a-z 0-9 - _ . (the envelope requires a non-empty ID).
        Assert.Equal(65, "x-65-characters-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx".Length);
        using var db = Context();

        Assert.Throws<ArgumentException>(() => Writer(db).Add(SampleEvents.All["PasswordChanged"], correlationId));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void Every_enum_used_in_an_event_is_covered_by_the_enum_value_test()
    {
        var used = typeof(DomainEvent).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(DomainEvent)) && !t.IsAbstract)
            .SelectMany(t => EnumsIn(t, []))
            .Distinct().OrderBy(t => t.Name).ToList();

        Assert.Equal(used, EnumVariants().Select(v => v.Enum).Distinct().OrderBy(t => t.Name));
    }

    [Fact]
    public void Every_enum_value_validates_against_the_schema()
    {
        // Samples use one value of each enum; a C# value missing from a schema's enum would
        // otherwise only fail in the worker.
        var failures = EnumVariants()
            .Select(v => (v.Label, Errors: EventSchemas.Errors(Writer().Add(v.Event).Payload)))
            .Where(r => r.Errors.Count > 0)
            .Select(r => $"{r.Label}: {string.Join("; ", r.Errors)}")
            .ToList();

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void AccessGranted_with_an_invite_validates()
    {
        // The other branch of inviteId's oneOf (the sample has null).
        var granted = (AccessGranted)SampleEvents.All["AccessGranted"] with { InviteId = Guid.NewGuid() };

        EventSchemas.AssertValid(Writer().Add(granted).Payload);
    }

    [Fact]
    public void ReminderDue_schema_loads_and_accepts_the_shape_the_worker_sends()
    {
        // EventBridge Scheduler sends ReminderDue, not the API; this checks the schema itself
        // resolves and constrains. The worker's own tests (T26) check what it schedules.
        const string valid = """
            {"eventId":"0190a000-0000-7000-8000-0000000000aa","eventType":"ReminderDue","schemaVersion":1,
             "occurredAt":"2026-11-30T09:00:00.000Z","correlationId":"worker-1",
             "data":{"bookingId":"0190a000-0000-7000-8000-000000000001","reminder":"24h","scheduledForStartUtc":"2026-12-01T09:00:00.000Z"}}
            """;
        Assert.Empty(EventSchemas.Errors(valid));
        Assert.NotEmpty(EventSchemas.Errors(valid.Replace("\"24h\"", "\"2h\"")));
    }

    private static IEnumerable<Type> EnumsIn(Type type, HashSet<Type> seen)
    {
        if (!seen.Add(type)) yield break;
        foreach (var property in type.GetProperties())
        {
            var t = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (t.IsEnum) yield return t;
            else if (t.Namespace == typeof(DomainEvent).Namespace)
                foreach (var nested in EnumsIn(t, seen)) yield return nested;
        }
    }

    private static IEnumerable<(Type Enum, string Label, DomainEvent Event)> EnumVariants()
    {
        var created = (BookingCreated)SampleEvents.All["BookingCreated"];
        foreach (var role in Enum.GetValues<ActorRole>())
            yield return (typeof(ActorRole), $"ActorRole.{role}", created with { CreatedBy = created.CreatedBy with { Role = role } });

        var cancelled = (BookingCancelled)SampleEvents.All["BookingCancelled"];
        foreach (var reason in Enum.GetValues<CancellationReason>())
            yield return (typeof(CancellationReason), $"CancellationReason.{reason}", cancelled with { Reason = reason });

        var granted = (AccessGranted)SampleEvents.All["AccessGranted"];
        foreach (var role in Enum.GetValues<GrantedRole>())
            yield return (typeof(GrantedRole), $"GrantedRole.{role}", granted with { Role = role });

        var changed = (PasswordChanged)SampleEvents.All["PasswordChanged"];
        foreach (var method in Enum.GetValues<PasswordChangeMethod>())
            yield return (typeof(PasswordChangeMethod), $"PasswordChangeMethod.{method}", changed with { Method = method });
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Non_UTC_times_are_rejected(DateTimeKind kind)
    {
        // R2: every time in an event is UTC; a local or unspecified time is a bug in the caller.
        var start = new DateTime(2026, 12, 1, 9, 0, 0, kind);
        var bad = new BookingLocalTimeChanged(SampleEvents.BookingId, Guid.NewGuid(), Guid.NewGuid(),
            new EventSlot(Guid.NewGuid(), start, start.AddHours(1)), "Europe/London", "Europe/Paris", Guid.NewGuid());
        using var db = Context();

        var error = Assert.Throws<InvalidOperationException>(() => Writer(db).Add(bad));
        Assert.Contains("UTC", error.Message);
        Assert.Empty(db.ChangeTracker.Entries()); // nothing staged
    }
}
