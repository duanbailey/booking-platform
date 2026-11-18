using System.Net;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Events;
using BookingPlatform.IntegrationTests.Infrastructure;
using BookingPlatform.Tests.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T5: the transactional outbox against MySQL (specs/06-events.md "Delivery"). An event
/// is written if and only if the caller's transaction commits, and the stored payload
/// validates against its JSON Schema.
/// </summary>
public class OutboxTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));

    private OutboxWriter Writer(BookingDbContext db) => new(db, _time, new HttpContextAccessor());

    private async Task<long> CountAsync(Guid eventId) => Convert.ToInt64(await fixture.Sql.ScalarAsync(
        "SELECT COUNT(*) FROM outbox WHERE id = @id", new { id = eventId.ToString() }));

    private static User NewUser(string email) => new()
    {
        Id = Guid.NewGuid(), Email = email, Name = "Ann Example", TimeZone = "Europe/London",
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Event_is_written_when_the_transaction_commits()
    {
        await using var db = fixture.Database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var message = Writer(db).Add(SampleEvents.All["BookingCreated"], correlationId: "commit-1");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        var row = await fixture.Database.CreateContext().Outbox.AsNoTracking().SingleAsync(m => m.Id == message.Id);
        Assert.Equal("BookingCreated", row.EventType);
        Assert.Equal(1, row.SchemaVersion);
        Assert.Equal(SampleEvents.BookingId.ToString(), row.MessageGroupId);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, row.CreatedAt);
        Assert.Null(row.PublishedAt);
        EventSchemas.AssertValid(row.Payload);
    }

    [Fact]
    public async Task Nothing_is_written_when_the_transaction_rolls_back()
    {
        await using var db = fixture.Database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var message = Writer(db).Add(SampleEvents.All["BookingCreated"]);
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.Outbox.CountAsync(m => m.Id == message.Id)); // visible inside the transaction

        await transaction.RollbackAsync();

        Assert.Equal(0, await CountAsync(message.Id));
    }

    [Fact]
    public async Task Nothing_is_written_when_the_transaction_is_abandoned()
    {
        Guid eventId;
        await using (var db = fixture.Database.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            eventId = Writer(db).Add(SampleEvents.All["PasswordChanged"]).Id;
            await db.SaveChangesAsync();
            // Disposed without commit, e.g. an exception after SaveChanges.
        }

        Assert.Equal(0, await CountAsync(eventId));
    }

    [Fact]
    public async Task Nothing_is_written_when_the_change_it_accompanies_fails()
    {
        // SaveChanges without an explicit transaction is still one transaction: a duplicate
        // email (unique index) fails the user insert and takes the event with it.
        var email = ApiFixture.NewEmail();
        await fixture.Sql.InsertUserAsync(email);

        await using var db = fixture.Database.CreateContext();
        var user = NewUser(email);
        db.Users.Add(user);
        var message = Writer(db).Add(new PasswordChanged(user.Id, PasswordChangeMethod.Changed));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(0, await CountAsync(message.Id));
    }

    [Fact]
    public async Task Event_and_change_are_saved_together_by_the_callers_SaveChanges()
    {
        await using var db = fixture.Database.CreateContext();
        var user = NewUser(ApiFixture.NewEmail());
        db.Users.Add(user);
        var message = Writer(db).Add(new PasswordChanged(user.Id, PasswordChangeMethod.Changed));

        Assert.Equal(0, await CountAsync(message.Id)); // Add alone writes nothing
        await db.SaveChangesAsync();

        Assert.Equal(1, await CountAsync(message.Id));
        Assert.Equal(1L, Convert.ToInt64(await fixture.Sql.ScalarAsync(
            "SELECT COUNT(*) FROM users WHERE id = @id", new { id = user.Id.ToString() })));
    }

    [Theory]
    [MemberData(nameof(SampleEvents.Names), MemberType = typeof(SampleEvents))]
    public async Task Every_event_still_validates_after_storage_in_the_JSON_column(string eventType)
    {
        // MySQL normalizes JSON columns (key order, whitespace); the relay publishes what is stored.
        await using var db = fixture.Database.CreateContext();
        var message = Writer(db).Add(SampleEvents.All[eventType]);
        await db.SaveChangesAsync();

        var stored = Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT payload FROM outbox WHERE id = @id", new { id = message.Id.ToString() }))!;
        EventSchemas.AssertValid(stored);
    }

    [Fact]
    public async Task Request_correlation_id_reaches_the_event()
    {
        var user = await fixture.Sql.InsertUserAsync();
        var client = fixture.Api.ClientFor(user);
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "t5-request-1");

        var body = await Json.ReadAsync(await client.PostAsync("/test/outbox", null), HttpStatusCode.OK);

        var payload = Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT payload->>'$.correlationId' FROM outbox WHERE id = @id",
            new { id = body.GetProperty("eventId").GetString() }));
        Assert.Equal("t5-request-1", payload);
    }
}
