using System.Globalization;
using System.Net;
using System.Text.Json;
using BookingPlatform.Api.Auth;
using BookingPlatform.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T29: Idempotency-Key on POST /v1/bookings (decided 2026-11-25; the IdempotencyKey parameter in
/// specs/04-api.openapi.yaml, idempotency_keys in specs/05-data-model.md). Optional, per caller; only a
/// created booking is remembered; a repeat is 201 with the booking as it is now; a different body is 422
/// IdempotencyKeyReused; keys last 24 hours. The business is in UTC; staff A and B are available 09:00-12:00
/// every day and the service lasts 60 minutes with 15-minute buffers (as in BookingsTests).
/// </summary>
public class IdempotencyKeyTests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>
{
    private const string Header = "Idempotency-Key";

    private sealed record Shop(Guid OwnerId, Guid BusinessId, Guid ServiceId, Guid StaffA, Guid StaffB, Guid CustomerId, string CustomerEmail);

    private DateOnly Day => DateOnly.FromDateTime(fixture.Now).AddDays(3);

    private static DateTime On(DateOnly date, int hour, int minute = 0) => date.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Utc);

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string NewKey() => Guid.NewGuid().ToString();

    private async Task<Shop> ShopAsync()
    {
        var owner = await fixture.Sql.InsertUserAsync(name: "Olive Owner");
        var business = await fixture.Sql.InsertBusinessAsync(owner);
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET time_zone = 'UTC' WHERE id = @business", new { business });
        var service = await fixture.Sql.InsertServiceAsync(business); // 60 minutes, 15-minute buffers

        async Task<Guid> StaffAsync()
        {
            var user = await fixture.Sql.InsertUserAsync(passwordHash: "hash", name: "Sam Staff");
            var staff = await fixture.Sql.InsertStaffAsync(business, user);
            await fixture.Sql.AssignStaffToServiceAsync(staff, service);
            foreach (var day in Enum.GetNames<DayOfWeek>())
            {
                await fixture.Sql.ExecuteAsync(
                    "INSERT INTO availability_rules (id, staff_id, day_of_week, start_local, end_local) VALUES (@id, @staff, @day, '09:00', '12:00')",
                    new { id = Guid.NewGuid(), staff, day });
            }
            return staff;
        }

        var a = await StaffAsync();
        var b = await StaffAsync();
        var email = ApiFixture.NewEmail();
        var customer = await fixture.Sql.InsertUserAsync(email, "hash", "Cara Customer");
        return new Shop(owner, business, service, a, b, customer, email);
    }

    private static Dictionary<string, object?> Body(Shop shop, Guid staffId, DateTime start, string? customerEmail = null, bool? overrideNotice = null)
    {
        var body = new Dictionary<string, object?> { ["serviceId"] = shop.ServiceId, ["staffId"] = staffId, ["startUtc"] = Iso(start) };
        if (customerEmail is not null) body["customerEmail"] = customerEmail;
        if (overrideNotice is not null) body["overrideMinimumNotice"] = overrideNotice;
        return body;
    }

    private Task<HttpResponseMessage> Book(Guid callerId, object body, params string[] keys)
    {
        if (keys.Contains("")) return BookWithEmptyKey(callerId, body);
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/bookings") { Content = JsonContent(body) };
        if (keys.Length > 0) request.Headers.TryAddWithoutValidation(Header, keys);
        return fixture.Api.ClientFor(callerId).SendAsync(request);
    }

    private static StringContent JsonContent(object body) => Json.Body(JsonSerializer.Serialize(body));

    /// <summary>
    /// HttpClient drops a header with an empty value before the in-memory server sees it, so this request is
    /// built on the server's HttpContext. (Kestrel does pass "Idempotency-Key:" through; checked with curl.)
    /// </summary>
    private async Task<HttpResponseMessage> BookWithEmptyKey(Guid callerId, object body)
    {
        var token = fixture.Api.Services.GetRequiredService<AccessTokenIssuer>().Issue(callerId).Token;
        var json = JsonSerializer.SerializeToUtf8Bytes(body);
        var context = await fixture.Api.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/v1/bookings";
            c.Request.Headers.Authorization = $"Bearer {token}";
            c.Request.Headers[Header] = "";
            c.Request.ContentType = "application/json";
            c.Request.Body = new MemoryStream(json);
        });
        Assert.True(context.Request.Headers.ContainsKey(Header)); // the header really was sent, empty
        var response = new HttpResponseMessage((HttpStatusCode)context.Response.StatusCode)
        {
            Content = new StreamContent(context.Response.Body),
        };
        return response;
    }

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        var body = await Json.ReadAsync(response, HttpStatusCode.Created);
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"/v1/bookings/{id}", response.Headers.Location?.OriginalString);
        return id;
    }

    private async Task<long> CountAsync(string sql, object parameters) => Convert.ToInt64(await fixture.Sql.ScalarAsync(sql, parameters));

    private Task<long> BookingCountAsync(Shop shop) =>
        CountAsync("SELECT COUNT(*) FROM bookings WHERE service_id = @service", new { service = shop.ServiceId });

    /// <summary>Exactly one booking, one Created history row and one BookingCreated event for this booking.</summary>
    private async Task AssertWrittenOnceAsync(Shop shop, Guid bookingId)
    {
        Assert.Equal(1, await BookingCountAsync(shop));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM booking_history WHERE booking_id = @bookingId", new { bookingId }));
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM outbox WHERE event_type = 'BookingCreated' AND message_group_id = @id", new { id = bookingId.ToString() }));
    }

    /// <summary>Runs <paramref name="test"/> with the clock moved forward, restoring it afterwards.</summary>
    private async Task WithClockAdvancedAsync(TimeSpan by, Func<Task> test)
    {
        fixture.Clock.Advance(by);
        try { await test(); }
        finally { fixture.Clock.Advance(-by); }
    }

    // ------------------------------------------------------------ Repeats

    [Fact]
    public async Task A_repeat_with_the_same_key_returns_the_same_booking_and_writes_nothing_more()
    {
        var shop = await ShopAsync();
        var key = NewKey();
        var body = Body(shop, shop.StaffA, On(Day, 10));

        var first = await Book(shop.CustomerId, body, key);
        var firstJson = await first.Content.ReadAsStringAsync();
        var id = await CreatedIdAsync(first);
        var repeat = await Book(shop.CustomerId, body, key);

        Assert.Equal(id, await CreatedIdAsync(repeat));
        Assert.Equal(firstJson, await repeat.Content.ReadAsStringAsync()); // unchanged booking: the same body
        await AssertWrittenOnceAsync(shop, id);
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM idempotency_keys WHERE user_id = @user AND idempotency_key = @key AND booking_id = @id",
            new { user = shop.CustomerId, key, id }));
    }

    [Fact]
    public async Task Without_a_key_a_repeat_is_a_new_request()
    {
        var shop = await ShopAsync();
        var body = Body(shop, shop.StaffA, On(Day, 10));

        await CreatedIdAsync(await Book(shop.CustomerId, body));
        await Json.ErrorAsync(await Book(shop.CustomerId, body), HttpStatusCode.Conflict, "SlotConflict"); // unchanged (R7)
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM idempotency_keys WHERE user_id = @user", new { user = shop.CustomerId }));
    }

    [Fact]
    public async Task A_repeat_returns_the_booking_as_it_is_now()
    {
        var shop = await ShopAsync();
        var key = NewKey();
        var body = Body(shop, shop.StaffA, On(Day, 10));
        var id = await CreatedIdAsync(await Book(shop.CustomerId, body, key));
        await fixture.Sql.ExecuteAsync(
            "UPDATE bookings SET status = 'Cancelled', cancelled_at = @now, cancelled_by = @owner, cancellation_reason = 'BusinessCancelled' WHERE id = @id",
            new { now = fixture.Now, owner = shop.OwnerId, id });

        var repeat = await Json.ReadAsync(await Book(shop.CustomerId, body, key), HttpStatusCode.Created);

        Assert.Equal(id, repeat.GetProperty("id").GetGuid());
        Assert.Equal("Cancelled", repeat.GetProperty("status").GetString());
        Assert.Equal("BusinessCancelled", repeat.GetProperty("cancellationReason").GetString());
        await AssertWrittenOnceAsync(shop, id);
    }

    [Fact]
    public async Task A_repeat_is_answered_before_the_other_checks()
    {
        var shop = await ShopAsync();
        var key = NewKey();
        var body = Body(shop, shop.StaffA, On(Day, 10), shop.CustomerEmail, overrideNotice: true);
        var staffUser = (Guid)(await fixture.Sql.RowAsync("SELECT user_id FROM staff WHERE id = @id", new { id = shop.StaffA }))!["user_id"]!;
        var id = await CreatedIdAsync(await Book(staffUser, body, key)); // staff books on behalf, with the override

        // Since then the staff member was removed (403/422 now) and the notice period grew past the start (R3).
        await fixture.Sql.RemoveStaffAsync(shop.StaffA);
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET min_notice_minutes = 525600 WHERE id = @id", new { id = shop.BusinessId });

        Assert.Equal(id, await CreatedIdAsync(await Book(staffUser, body, key)));
        await AssertWrittenOnceAsync(shop, id);
    }

    [Fact]
    public async Task The_same_request_written_differently_is_a_repeat()
    {
        var shop = await ShopAsync();
        var key = NewKey();
        var start = On(Day, 10);
        var id = await CreatedIdAsync(await Book(shop.OwnerId, Body(shop, shop.StaffA, start, shop.CustomerEmail), key));

        // Another spelling of the same instant, the email's case and spaces, an explicit default and a confirm list.
        var same = new Dictionary<string, object?>
        {
            ["serviceId"] = shop.ServiceId, ["staffId"] = shop.StaffA,
            ["startUtc"] = start.ToString("yyyy-MM-dd'T'HH:mm:ss'.000Z'", CultureInfo.InvariantCulture),
            ["customerEmail"] = $"  {shop.CustomerEmail.ToUpperInvariant()} ", ["overrideMinimumNotice"] = false,
            ["confirm"] = new { affectedBookingIds = new[] { Guid.NewGuid() } },
        };
        Assert.Equal(id, await CreatedIdAsync(await Book(shop.OwnerId, same, key)));
        await AssertWrittenOnceAsync(shop, id);
    }

    // ------------------------------------------------------------ A different request under the same key

    public static TheoryData<string> Changes => ["startUtc", "staffId", "customerEmail", "noCustomerEmail", "overrideMinimumNotice"];

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task The_same_key_for_a_different_request_is_422_and_books_nothing(string change)
    {
        var shop = await ShopAsync();
        var other = ApiFixture.NewEmail();
        await fixture.Sql.InsertUserAsync(other, "hash", "Otto Other");
        var key = NewKey();
        var id = await CreatedIdAsync(await Book(shop.OwnerId, Body(shop, shop.StaffA, On(Day, 9), shop.CustomerEmail), key));

        var body = change switch
        {
            "startUtc" => Body(shop, shop.StaffA, On(Day, 11), shop.CustomerEmail),
            "staffId" => Body(shop, shop.StaffB, On(Day, 9), shop.CustomerEmail),
            "customerEmail" => Body(shop, shop.StaffA, On(Day, 9), other),
            "noCustomerEmail" => Body(shop, shop.StaffA, On(Day, 9)),
            _ => Body(shop, shop.StaffA, On(Day, 9), shop.CustomerEmail, overrideNotice: true),
        };
        var error = await Json.ErrorAsync(await Book(shop.OwnerId, body, key), HttpStatusCode.UnprocessableEntity, "IdempotencyKeyReused");

        Assert.Equal(JsonValueKind.Null, error.GetProperty("rule").ValueKind);
        await AssertWrittenOnceAsync(shop, id);
    }

    [Fact]
    public async Task Keys_belong_to_the_caller()
    {
        var shop = await ShopAsync();
        var other = await fixture.Sql.InsertUserAsync();
        var key = NewKey();
        var mine = await CreatedIdAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 9)), key));

        // The same key from someone else is their own key: a new booking, not 422 or a repeat of mine.
        var theirs = await CreatedIdAsync(await Book(other, Body(shop, shop.StaffA, On(Day, 11)), key));

        Assert.NotEqual(mine, theirs);
        Assert.Equal(2, await BookingCountAsync(shop));
    }

    [Fact]
    public async Task Keys_are_case_sensitive()
    {
        var shop = await ShopAsync();
        await CreatedIdAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 9)), "key-abc"));
        await CreatedIdAsync(await Book(shop.CustomerId, Body(shop, shop.StaffB, On(Day, 11)), "KEY-ABC"));
        Assert.Equal(2, await BookingCountAsync(shop));
    }

    // ------------------------------------------------------------ Failures record nothing

    [Fact]
    public async Task A_failed_request_records_nothing_so_the_key_can_be_retried_with_another_body()
    {
        var shop = await ShopAsync();
        var key = NewKey();
        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 9, 10)), key),
            HttpStatusCode.UnprocessableEntity, "SlotUnavailable"); // R8: not an offered start

        await CreatedIdAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 9)), key));
        Assert.Equal(1, await BookingCountAsync(shop));
    }

    [Fact]
    public async Task R4_a_confirmation_request_is_retried_with_the_same_key_and_confirm()
    {
        var shop = await ShopAsync();
        // A cancellation window of a year puts the customer's first booking inside it (R4).
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET cancellation_window_hours = 8760 WHERE id = @id", new { id = shop.BusinessId });
        var original = await CreatedIdAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 9)), NewKey()));
        var key = NewKey();
        var body = Body(shop, shop.StaffB, On(Day, 11));

        await Json.ErrorAsync(await Book(shop.CustomerId, body, key), HttpStatusCode.Conflict, "ConfirmationRequired");
        body["confirm"] = new { affectedBookingIds = new[] { original } };
        var rebooked = await CreatedIdAsync(await Book(shop.CustomerId, body, key));

        Assert.Equal(rebooked, await CreatedIdAsync(await Book(shop.CustomerId, body, key))); // and that is remembered
        Assert.Equal(2, await BookingCountAsync(shop));
    }

    // ------------------------------------------------------------ 24 hours

    [Fact]
    public async Task A_key_is_remembered_for_24_hours()
    {
        var shop = await ShopAsync();
        var key = NewKey();
        var start = On(Day, 9); // fixed before the clock moves
        var later = On(Day, 11);
        var id = await CreatedIdAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, start), key));

        await WithClockAdvancedAsync(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1), async () =>
        {
            Assert.Equal(id, await CreatedIdAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, start), key)));
            await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, later), key),
                HttpStatusCode.UnprocessableEntity, "IdempotencyKeyReused");
        });

        // At exactly 24 hours the key is new: the same body is a new request (R7 conflict with the booking it made),
        // and another body books and takes the key over.
        await WithClockAdvancedAsync(TimeSpan.FromHours(24), async () =>
        {
            await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, start), key), HttpStatusCode.Conflict, "SlotConflict");
            var second = await CreatedIdAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, later), key));
            Assert.NotEqual(id, second);
            Assert.Equal(second, await CreatedIdAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, later), key)));
            var row = (await fixture.Sql.RowAsync(
                "SELECT booking_id, created_at FROM idempotency_keys WHERE user_id = @user AND idempotency_key = @key",
                new { user = shop.CustomerId, key }))!;
            Assert.Equal(second, (Guid)row["booking_id"]!);
            Assert.Equal(fixture.Now, (DateTime)row["created_at"]!);
        });
    }

    [Fact]
    public async Task An_expired_key_is_deleted_in_its_own_transaction_before_the_booking_commits()
    {
        // T31 review (2026-11-26): the cleanup may delete an expired row at any moment, and a DELETE inside the
        // booking's REPEATABLE READ transaction that then matches no row takes a gap lock (deadlock risk, T29).
        // So the expired row is deleted in its own READ COMMITTED transaction: it is gone while the booking
        // still waits (here at its insert, whose foreign key needs the held service row).
        var shop = await ShopAsync();
        var key = NewKey();
        var keyRow = new { user = shop.CustomerId, key };
        await CreatedIdAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 9)), key));
        await fixture.Sql.ExecuteAsync(
            "UPDATE idempotency_keys SET created_at = @expired WHERE user_id = @user AND idempotency_key = @key",
            new { expired = fixture.Now.AddHours(-24), user = shop.CustomerId, key });
        Task<HttpResponseMessage> second;

        await using (var held = await fixture.Sql.LockRowAsync("services", shop.ServiceId))
        {
            second = Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 11)), key);
            await fixture.Sql.WaitForLockWaitsAsync("services", 1);
            Assert.False(second.IsCompleted);
            Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM idempotency_keys WHERE user_id = @user AND idempotency_key = @key", keyRow));
        }

        var id = await CreatedIdAsync(await second);
        Assert.Equal(id, (Guid)(await fixture.Sql.ScalarAsync(
            "SELECT booking_id FROM idempotency_keys WHERE user_id = @user AND idempotency_key = @key", keyRow))!);
    }

    // ------------------------------------------------------------ Invalid keys

    public static TheoryData<string[]> InvalidKeys => new()
    {
        new[] { "" },
        new[] { new string('k', 65) },
        new[] { "has space" },
        new[] { "colon:key" },
        new[] { "naïve" },
        new[] { "one", "two" },
    };

    [Theory]
    [MemberData(nameof(InvalidKeys))]
    public async Task An_invalid_key_is_400_on_the_header(string[] keys)
    {
        var shop = await ShopAsync();
        var error = await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10)), keys),
            HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Equal([Header], Json.FieldNames(error));
        Assert.Equal(0, await BookingCountAsync(shop));
    }

    [Fact]
    public async Task A_64_character_key_of_every_allowed_character_is_accepted()
    {
        var shop = await ShopAsync();
        var key = ("AZaz09._-" + new string('x', 64))[..64];
        var id = await CreatedIdAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10)), key));
        Assert.Equal(id, await CreatedIdAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10)), key)));
    }

    // ------------------------------------------------------------ Concurrent repeats

    [Fact]
    public async Task A_concurrent_repeat_waits_for_the_first_request_and_returns_its_booking()
    {
        var shop = await ShopAsync();
        var key = NewKey();
        var body = Body(shop, shop.StaffA, On(Day, 10));
        Task<HttpResponseMessage> first, second;

        // Hold the customer's row (ADR 0002 takes it first) until both requests wait for it, so they truly overlap.
        await using (var held = await fixture.Sql.LockUserRowAsync(shop.CustomerId))
        {
            first = Book(shop.CustomerId, body, key);
            second = Book(shop.CustomerId, body, key);
            await fixture.Sql.WaitForLockWaitsAsync("users", 2);
            Assert.False(first.IsCompleted || second.IsCompleted);
        }

        var id = await CreatedIdAsync(await first);
        Assert.Equal(id, await CreatedIdAsync(await second));
        await AssertWrittenOnceAsync(shop, id);
    }

    [Fact]
    public async Task A_concurrent_different_request_with_the_same_key_gets_422()
    {
        var shop = await ShopAsync();
        var otherEmail = ApiFixture.NewEmail();
        await fixture.Sql.InsertUserAsync(otherEmail, "hash", "Otto Other");
        var key = NewKey();
        Task<HttpResponseMessage> first, second;

        // The owner books two customers with two staff members under one key, so the requests share no lock and
        // both pass the key check under their locks. Holding the service row stops both at the booking insert
        // (its foreign key takes a shared lock on the service), so neither has committed yet, and the primary
        // key settles it when they go on.
        await using (var held = await fixture.Sql.LockRowAsync("services", shop.ServiceId))
        {
            first = Book(shop.OwnerId, Body(shop, shop.StaffA, On(Day, 9), shop.CustomerEmail), key);
            second = Book(shop.OwnerId, Body(shop, shop.StaffB, On(Day, 11), otherEmail), key);
            await fixture.Sql.WaitForLockWaitsAsync("services", 2);
            Assert.False(first.IsCompleted || second.IsCompleted);
        }

        var responses = await Task.WhenAll(first, second);
        var winner = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var loser = Assert.Single(responses, r => r.StatusCode != HttpStatusCode.Created);
        await Json.ErrorAsync(loser, HttpStatusCode.UnprocessableEntity, "IdempotencyKeyReused");
        await AssertWrittenOnceAsync(shop, await CreatedIdAsync(winner));
    }
}
