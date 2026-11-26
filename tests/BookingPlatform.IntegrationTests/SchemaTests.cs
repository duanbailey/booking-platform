using BookingPlatform.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T2: the initial migration's keys, unique constraints, foreign keys, enums,
/// defaults and checks, tested with raw SQL against a migrated MySQL database.
/// </summary>
public class SchemaTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private readonly Sql _sql = new(database.ConnectionString);

    // ------------------------------------------------------------ Migrations

    [Fact]
    public async Task Ef_model_has_no_changes_missing_from_migrations()
    {
        await using var context = database.CreateContext();
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Every_table_in_the_data_model_exists()
    {
        string[] tables =
        [
            "users", "user_invites", "password_resets", "refresh_tokens", "platform_admins", "businesses", "services", "staff", "staff_services",
            "availability_rules", "availability_exceptions", "time_off", "time_zone_changes", "outbox",
            "bookings", "idempotency_keys", "notifications", "booking_history",
        ];
        foreach (var table in tables)
        {
            var count = await _sql.ScalarAsync(
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @table",
                new { table });
            Assert.True(Convert.ToInt64(count) == 1, $"missing table {table}");
        }
    }

    // ------------------------------------------------------------ Users and roles

    [Fact]
    public async Task Duplicate_email_is_rejected()
    {
        await _sql.InsertUserAsync("dup@example.test");
        var error = await Assert.ThrowsAsync<MySqlException>(() => _sql.InsertUserAsync("dup@example.test"));
        Assert.Equal(ExpectedError.DuplicateKey, error.Number);
    }

    [Fact]
    public async Task Email_uniqueness_ignores_case()
    {
        await _sql.InsertUserAsync("Case@Example.test");
        var error = await Assert.ThrowsAsync<MySqlException>(() => _sql.InsertUserAsync("case@example.test"));
        Assert.Equal(ExpectedError.DuplicateKey, error.Number);
    }

    [Fact]
    public async Task Email_uses_the_case_insensitive_accent_sensitive_collation()
    {
        // specs/05-data-model.md: utf8mb4_0900_as_ci. Oracle's EF provider drops column
        // collations from migrations, so this is set with explicit SQL (FixEmailCollation).
        Assert.Equal("utf8mb4_0900_as_ci", await _sql.ScalarAsync(
            "SELECT COLLATION_NAME FROM information_schema.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'users' AND COLUMN_NAME = 'email'"));
    }

    [Fact]
    public async Task Idempotency_keys_are_case_sensitive_ascii()
    {
        // T29, specs/05-data-model.md: ascii_bin, set with explicit SQL (the provider drops column collations).
        Assert.Equal("ascii_bin", await _sql.ScalarAsync(
            "SELECT COLLATION_NAME FROM information_schema.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'idempotency_keys' AND COLUMN_NAME = 'idempotency_key'"));
    }

    [Fact]
    public async Task Emails_differing_only_in_accents_are_distinct()
    {
        var suffix = Guid.NewGuid().ToString("N");
        await _sql.InsertUserAsync($"ann-{suffix}@example.test");
        await _sql.InsertUserAsync($"ánn-{suffix}@example.test"); // no duplicate-key error

        Assert.Equal(1L, Convert.ToInt64(await _sql.ScalarAsync(
            "SELECT COUNT(*) FROM users WHERE email = @email", new { email = $"ANN-{suffix}@example.test" })));
    }

    [Fact]
    public async Task User_without_password_hash_is_allowed_until_invite_is_accepted()
    {
        var user = await _sql.InsertUserAsync();
        Assert.Equal(DBNull.Value,
            await _sql.ScalarAsync("SELECT password_hash FROM users WHERE id = @user", new { user }));
    }

    [Fact]
    public async Task Second_business_for_the_same_owner_is_rejected()
    {
        var owner = await _sql.InsertUserAsync();
        await _sql.InsertBusinessAsync(owner);
        var error = await Assert.ThrowsAsync<MySqlException>(() => _sql.InsertBusinessAsync(owner));
        Assert.Equal(ExpectedError.DuplicateKey, error.Number);
    }

    [Fact]
    public async Task Duplicate_staff_row_for_the_same_user_and_business_is_rejected()
    {
        var business = await _sql.InsertBusinessAsync();
        var user = await _sql.InsertUserAsync();
        await _sql.InsertStaffAsync(business, user);
        var error = await Assert.ThrowsAsync<MySqlException>(() => _sql.InsertStaffAsync(business, user));
        Assert.Equal(ExpectedError.DuplicateKey, error.Number);
    }

    [Fact]
    public async Task A_user_can_be_staff_at_two_businesses()
    {
        var user = await _sql.InsertUserAsync();
        await _sql.InsertStaffAsync(await _sql.InsertBusinessAsync(), user);
        await _sql.InsertStaffAsync(await _sql.InsertBusinessAsync(), user);
    }

    private const string InsertAdminSql =
        "INSERT INTO platform_admins (user_id, granted_by, granted_at) VALUES (@user, @grantedBy, @now)";

    [Fact]
    public async Task Admin_role_is_one_row_per_user()
    {
        var user = await _sql.InsertUserAsync();
        await _sql.ExecuteAsync(InsertAdminSql, new { user, grantedBy = (Guid?)null, now = Sql.Now });
        Assert.Equal(ExpectedError.DuplicateKey,
            await _sql.FailsWithAsync(InsertAdminSql, new { user, grantedBy = (Guid?)null, now = Sql.Now }));
    }

    [Fact]
    public async Task Admin_is_granted_by_an_existing_user_or_null_for_the_first_admin()
    {
        var first = await _sql.InsertUserAsync();
        await _sql.ExecuteAsync(InsertAdminSql, new { user = first, grantedBy = (Guid?)null, now = Sql.Now });

        var second = await _sql.InsertUserAsync();
        await _sql.ExecuteAsync(InsertAdminSql, new { user = second, grantedBy = (Guid?)first, now = Sql.Now });

        var third = await _sql.InsertUserAsync();
        Assert.Equal(ExpectedError.ForeignKeyViolation, await _sql.FailsWithAsync(InsertAdminSql,
            new { user = third, grantedBy = (Guid?)Guid.NewGuid(), now = Sql.Now }));
    }

    [Fact]
    public async Task Invite_token_hash_is_unique_and_null_until_the_worker_sets_it()
    {
        var user = await _sql.InsertUserAsync();
        var creator = await _sql.InsertUserAsync();
        const string insert =
            "INSERT INTO user_invites (id, user_id, token_hash, expires_at, used_at, created_by, created_at) " +
            "VALUES (@id, @user, @hash, NULL, NULL, @creator, @now)";
        // Two invites the worker has not sent yet: no token hash.
        await _sql.ExecuteAsync(insert, new { id = Guid.NewGuid(), user, hash = (string?)null, creator, now = Sql.Now });
        await _sql.ExecuteAsync(insert, new { id = Guid.NewGuid(), user, hash = (string?)null, creator, now = Sql.Now });

        await _sql.ExecuteAsync(insert, new { id = Guid.NewGuid(), user, hash = "abc", creator, now = Sql.Now });
        Assert.Equal(ExpectedError.DuplicateKey,
            await _sql.FailsWithAsync(insert, new { id = Guid.NewGuid(), user, hash = "abc", creator, now = Sql.Now }));
    }

    [Fact]
    public async Task Password_reset_token_hash_is_unique_and_null_until_the_worker_sets_it()
    {
        // T7a: specs/05-data-model.md password_resets.
        var user = await _sql.InsertUserAsync();
        const string insert =
            "INSERT INTO password_resets (id, user_id, token_hash, expires_at, used_at, created_at) " +
            "VALUES (@id, @user, @hash, NULL, NULL, @now)";
        await _sql.ExecuteAsync(insert, new { id = Guid.NewGuid(), user, hash = (string?)null, now = Sql.Now });
        await _sql.ExecuteAsync(insert, new { id = Guid.NewGuid(), user, hash = (string?)null, now = Sql.Now });

        await _sql.ExecuteAsync(insert, new { id = Guid.NewGuid(), user, hash = "abc", now = Sql.Now });
        Assert.Equal(ExpectedError.DuplicateKey,
            await _sql.FailsWithAsync(insert, new { id = Guid.NewGuid(), user, hash = "abc", now = Sql.Now }));
    }

    [Fact]
    public async Task Password_reset_must_belong_to_an_existing_user()
    {
        Assert.Equal(ExpectedError.ForeignKeyViolation, await _sql.FailsWithAsync(
            "INSERT INTO password_resets (id, user_id, token_hash, expires_at, used_at, created_at) " +
            "VALUES (@id, @user, NULL, NULL, NULL, @now)",
            new { id = Guid.NewGuid(), user = Guid.NewGuid(), now = Sql.Now }));
    }

    // ------------------------------------------------------------ Business settings

    [Fact]
    public async Task Business_settings_take_their_defaults() // R3, R4, R8
    {
        var business = await _sql.InsertBusinessAsync();

        async Task<long> Setting(string column) => Convert.ToInt64(
            await _sql.ScalarAsync($"SELECT {column} FROM businesses WHERE id = @business", new { business }));

        Assert.Equal(120, await Setting("min_notice_minutes"));       // R3
        Assert.Equal(24, await Setting("cancellation_window_hours")); // R4
        Assert.Equal(21, await Setting("max_advance_days"));          // R8
        Assert.Equal(30, await Setting("slot_step_minutes"));         // R8
    }

    [Fact]
    public async Task Business_time_zone_is_required() // R2
    {
        var owner = await _sql.InsertUserAsync();
        var error = await _sql.FailsWithAsync(
            "INSERT INTO businesses (id, name, owner_user_id, time_zone, created_at) VALUES (@id, 'B', @owner, NULL, @now)",
            new { id = Guid.NewGuid(), owner, now = Sql.Now });
        Assert.Equal(ExpectedError.ColumnCannotBeNull, error);
    }

    [Theory]
    [InlineData("min_notice_minutes", -1)]
    [InlineData("cancellation_window_hours", -1)]
    [InlineData("max_advance_days", 0)]
    [InlineData("slot_step_minutes", 0)]
    [InlineData("min_notice_minutes", 525601)]     // T8 maxima, decided 2026-11-19
    [InlineData("cancellation_window_hours", 8761)]
    [InlineData("max_advance_days", 3651)]
    [InlineData("slot_step_minutes", 1441)]
    public async Task Business_settings_outside_their_range_are_rejected(string column, int value)
    {
        var business = await _sql.InsertBusinessAsync();
        var error = await _sql.FailsWithAsync(
            $"UPDATE businesses SET {column} = @value WHERE id = @business", new { value, business });
        Assert.Equal(ExpectedError.CheckViolation, error);
    }

    // ------------------------------------------------------------ Services and staff

    [Theory]
    [InlineData("duration_minutes", 0)]
    [InlineData("buffer_before_minutes", -1)]
    [InlineData("buffer_after_minutes", -1)]
    [InlineData("duration_minutes", 1441)]      // T9 maxima, decided 2026-11-19
    [InlineData("buffer_before_minutes", 1441)]
    [InlineData("buffer_after_minutes", 1441)]
    public async Task Service_durations_outside_their_range_are_rejected(string column, int value)
    {
        var service = await _sql.InsertServiceAsync(await _sql.InsertBusinessAsync());
        var error = await _sql.FailsWithAsync(
            $"UPDATE services SET {column} = @value WHERE id = @service", new { value, service });
        Assert.Equal(ExpectedError.CheckViolation, error);
    }

    [Fact]
    public async Task Staff_can_be_linked_to_a_service_only_once()
    {
        var business = await _sql.InsertBusinessAsync();
        var staff = await _sql.InsertStaffAsync(business);
        var service = await _sql.InsertServiceAsync(business);
        const string insert = "INSERT INTO staff_services (staff_id, service_id) VALUES (@staff, @service)";
        await _sql.ExecuteAsync(insert, new { staff, service });
        Assert.Equal(ExpectedError.DuplicateKey, await _sql.FailsWithAsync(insert, new { staff, service }));
    }

    // ------------------------------------------------------------ Availability and time off

    [Fact]
    public async Task Weekly_availability_block_must_end_after_it_starts_on_a_named_day() // R8
    {
        var staff = await _sql.InsertStaffAsync(await _sql.InsertBusinessAsync());
        const string insert =
            "INSERT INTO availability_rules (id, staff_id, day_of_week, start_local, end_local) " +
            "VALUES (@id, @staff, @day, @start, @end)";
        await _sql.ExecuteAsync(insert,
            new { id = Guid.NewGuid(), staff, day = "Monday", start = "09:00", end = "17:00" });
        Assert.Equal(ExpectedError.CheckViolation, await _sql.FailsWithAsync(insert,
            new { id = Guid.NewGuid(), staff, day = "Monday", start = "17:00", end = "09:00" }));
        Assert.Equal(ExpectedError.DataTruncated, await _sql.FailsWithAsync(insert,
            new { id = Guid.NewGuid(), staff, day = "Funday", start = "09:00", end = "17:00" }));
    }

    [Fact]
    public async Task Date_exception_is_either_a_time_range_or_a_whole_day_null_row() // R8
    {
        var staff = await _sql.InsertStaffAsync(await _sql.InsertBusinessAsync());
        const string insert =
            "INSERT INTO availability_exceptions (id, staff_id, date, start_local, end_local) " +
            "VALUES (@id, @staff, '2026-12-24', @start, @end)";
        await _sql.ExecuteAsync(insert,
            new { id = Guid.NewGuid(), staff, start = (string?)null, end = (string?)null });
        await _sql.ExecuteAsync(insert, new { id = Guid.NewGuid(), staff, start = "09:00", end = "12:00" });

        Assert.Equal(ExpectedError.CheckViolation, await _sql.FailsWithAsync(insert,
            new { id = Guid.NewGuid(), staff, start = "09:00", end = (string?)null }));
        Assert.Equal(ExpectedError.CheckViolation, await _sql.FailsWithAsync(insert,
            new { id = Guid.NewGuid(), staff, start = "12:00", end = "09:00" }));
    }

    [Fact]
    public async Task Time_off_must_end_after_it_starts()
    {
        var staff = await _sql.InsertStaffAsync(await _sql.InsertBusinessAsync());
        var error = await _sql.FailsWithAsync(
            "INSERT INTO time_off (id, staff_id, start_utc, end_utc, created_at) VALUES (@id, @staff, @start, @start, @now)",
            new { id = Guid.NewGuid(), staff, start = Sql.Now.AddDays(1), now = Sql.Now });
        Assert.Equal(ExpectedError.CheckViolation, error);
    }

    // ------------------------------------------------------------ Bookings

    [Fact]
    public async Task Valid_confirmed_booking_is_accepted()
    {
        var parameters = Sql.BookingParameters(await _sql.InsertBookingFixtureAsync());
        Assert.Equal(1, await _sql.ExecuteAsync(Sql.InsertBookingSql, parameters));
    }

    [Fact]
    public async Task Booking_must_reference_an_existing_staff_member()
    {
        var parameters = Sql.BookingParameters(await _sql.InsertBookingFixtureAsync());
        parameters["staff"] = Guid.NewGuid();
        Assert.Equal(ExpectedError.ForeignKeyViolation, await _sql.FailsWithAsync(Sql.InsertBookingSql, parameters));
    }

    [Fact]
    public async Task Booking_status_outside_the_enum_is_rejected()
    {
        var parameters = Sql.BookingParameters(await _sql.InsertBookingFixtureAsync());
        parameters["status"] = "Pending";
        Assert.Equal(ExpectedError.DataTruncated, await _sql.FailsWithAsync(Sql.InsertBookingSql, parameters));
    }

    [Fact]
    public async Task Booking_must_end_after_it_starts() // R1: half-open, non-empty interval
    {
        var parameters = Sql.BookingParameters(await _sql.InsertBookingFixtureAsync());
        parameters["end"] = parameters["start"];
        parameters["blockedEnd"] = parameters["start"];
        Assert.Equal(ExpectedError.CheckViolation, await _sql.FailsWithAsync(Sql.InsertBookingSql, parameters));
    }

    [Fact]
    public async Task Blocked_interval_must_contain_the_booking() // R1, ADR 0004: buffers are never negative
    {
        var fixture = await _sql.InsertBookingFixtureAsync();

        var startsLate = Sql.BookingParameters(fixture);
        startsLate["blockedStart"] = ((DateTime)startsLate["start"]!).AddMinutes(5);
        Assert.Equal(ExpectedError.CheckViolation, await _sql.FailsWithAsync(Sql.InsertBookingSql, startsLate));

        var endsEarly = Sql.BookingParameters(fixture);
        endsEarly["blockedEnd"] = ((DateTime)endsEarly["end"]!).AddMinutes(-5);
        Assert.Equal(ExpectedError.CheckViolation, await _sql.FailsWithAsync(Sql.InsertBookingSql, endsEarly));
    }

    [Fact]
    public async Task Confirmed_booking_cannot_have_cancellation_details()
    {
        var fixture = await _sql.InsertBookingFixtureAsync();
        var parameters = Sql.BookingParameters(fixture);
        parameters["cancelledAt"] = Sql.Now;
        parameters["cancelledBy"] = fixture.CustomerId;
        parameters["reason"] = "CustomerCancelled";
        Assert.Equal(ExpectedError.CheckViolation, await _sql.FailsWithAsync(Sql.InsertBookingSql, parameters));
    }

    [Theory]
    [InlineData("cancelledAt")]
    [InlineData("cancelledBy")]
    [InlineData("reason")]
    public async Task Cancelled_booking_requires_every_cancellation_column(string missing)
    {
        var fixture = await _sql.InsertBookingFixtureAsync();
        var parameters = CancelledBooking(fixture, "CustomerCancelled");
        parameters[missing] = null;
        Assert.Equal(ExpectedError.CheckViolation, await _sql.FailsWithAsync(Sql.InsertBookingSql, parameters));
    }

    [Theory]
    [InlineData("CustomerCancelled")]
    [InlineData("BusinessCancelled")]
    [InlineData("TimeOff")]
    [InlineData("AvailabilityChanged")]
    [InlineData("StaffRemoved")]
    public async Task Cancelled_booking_accepts_every_cancellation_reason(string reason) // R4, R6
    {
        var fixture = await _sql.InsertBookingFixtureAsync();
        Assert.Equal(1, await _sql.ExecuteAsync(Sql.InsertBookingSql, CancelledBooking(fixture, reason)));
    }

    private static Dictionary<string, object?> CancelledBooking(Sql.BookingFixture fixture, string reason)
    {
        var parameters = Sql.BookingParameters(fixture);
        parameters["status"] = "Cancelled";
        parameters["cancelledAt"] = Sql.Now;
        parameters["cancelledBy"] = fixture.CustomerId;
        parameters["reason"] = reason;
        return parameters;
    }

    [Fact]
    public async Task Booking_history_previous_slot_is_set_only_for_reschedules() // ADR 0003
    {
        var fixture = await _sql.InsertBookingFixtureAsync();
        var booking = Sql.BookingParameters(fixture);
        await _sql.ExecuteAsync(Sql.InsertBookingSql, booking);

        const string insert =
            "INSERT INTO booking_history (id, booking_id, type, actor_user_id, actor_role, at, staff_id, " +
            "start_utc, end_utc, previous_staff_id, previous_start_utc, previous_end_utc, " +
            "minimum_notice_overridden) VALUES (@id, @booking, @type, @actor, 'Customer', @now, @staff, " +
            "@start, @end, @prevStaff, @prevStart, @prevEnd, 0)";
        var created = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid(), ["booking"] = booking["id"], ["type"] = "Created",
            ["actor"] = fixture.CustomerId, ["now"] = Sql.Now, ["staff"] = fixture.StaffId,
            ["start"] = booking["start"], ["end"] = booking["end"],
            ["prevStaff"] = null, ["prevStart"] = null, ["prevEnd"] = null,
        };
        await _sql.ExecuteAsync(insert, created);

        var rescheduledWithoutPrevious = new Dictionary<string, object?>(created)
        {
            ["id"] = Guid.NewGuid(), ["type"] = "Rescheduled",
        };
        Assert.Equal(ExpectedError.CheckViolation, await _sql.FailsWithAsync(insert, rescheduledWithoutPrevious));

        var createdWithPrevious = new Dictionary<string, object?>(created)
        {
            ["id"] = Guid.NewGuid(), ["prevStaff"] = fixture.StaffId,
            ["prevStart"] = booking["start"], ["prevEnd"] = booking["end"],
        };
        Assert.Equal(ExpectedError.CheckViolation, await _sql.FailsWithAsync(insert, createdWithPrevious));
    }

    // ------------------------------------------------------------ Outbox and notifications

    [Fact]
    public async Task Bookings_have_an_index_for_staff_lists_ordered_by_start()
    {
        // T16 code review: GET /v1/businesses/{id}/bookings filters by staff member(s) and orders by (start, id);
        // InnoDB appends the primary key, so (staff_id, start_utc) serves the order and the keyset.
        var columns = await _sql.ScalarAsync(
            "SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) FROM information_schema.STATISTICS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bookings' " +
            "GROUP BY INDEX_NAME HAVING GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) = 'staff_id,start_utc'");
        Assert.Equal("staff_id,start_utc", columns);
    }

    [Fact]
    public async Task Idempotency_keys_have_an_index_for_the_cleanup()
    {
        // T31: the cleanup deletes by created_at; the primary key starts with user_id, so without this index the
        // delete would scan the table.
        var columns = await _sql.ScalarAsync(
            "SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) FROM information_schema.STATISTICS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'idempotency_keys' " +
            "GROUP BY INDEX_NAME HAVING GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) = 'created_at'");
        Assert.Equal("created_at", columns);
    }

    [Fact]
    public async Task Services_have_an_index_for_the_list_order()
    {
        // T9: GET /v1/businesses/{id}/services filters by business and orders by (name, id).
        var columns = await _sql.ScalarAsync(
            "SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) FROM information_schema.STATISTICS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'services' " +
            "GROUP BY INDEX_NAME HAVING GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) = 'business_id,name,id'");
        Assert.Equal("business_id,name,id", columns);
    }

    [Fact]
    public async Task Each_event_notifies_each_recipient_at_most_once()
    {
        var recipient = await _sql.InsertUserAsync();
        var eventId = Guid.NewGuid();
        const string insert =
            "INSERT INTO notifications (id, event_id, recipient_user_id, kind, status, created_at) " +
            "VALUES (@id, @eventId, @recipient, 'Confirmation', 'Pending', @now)";
        await _sql.ExecuteAsync(insert, new { id = Guid.NewGuid(), eventId, recipient, now = Sql.Now });
        Assert.Equal(ExpectedError.DuplicateKey,
            await _sql.FailsWithAsync(insert, new { id = Guid.NewGuid(), eventId, recipient, now = Sql.Now }));
    }

    [Theory]
    [InlineData("PasswordReset")]
    [InlineData("PasswordChanged")]
    public async Task Notification_kind_includes_the_account_emails(string kind)
    {
        // T7a: the kinds added for recovery and password change (specs/05-data-model.md notifications).
        var recipient = await _sql.InsertUserAsync();
        await _sql.ExecuteAsync(
            "INSERT INTO notifications (id, event_id, recipient_user_id, kind, status, created_at) " +
            "VALUES (@id, @eventId, @recipient, @kind, 'Pending', @now)",
            new { id = Guid.NewGuid(), eventId = Guid.NewGuid(), recipient, kind, now = Sql.Now });
    }

    [Fact]
    public async Task Outbox_seq_is_a_unique_auto_increment_and_the_relay_scans_have_indexes()
    {
        // T21 (decided 2026-11-23): seq is assigned by the database; the relay reads unpublished rows by seq and
        // checks for an earlier unpublished row in a group.
        Assert.Equal("auto_increment", await _sql.ScalarAsync(
            "SELECT EXTRA FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'outbox' AND COLUMN_NAME = 'seq'"));
        var indexes = (await _sql.RowsAsync(
                "SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) AS cols, MIN(NON_UNIQUE) AS non_unique FROM information_schema.STATISTICS " +
                "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'outbox' GROUP BY INDEX_NAME"))
            .ToDictionary(r => Convert.ToString(r["cols"])!, r => Convert.ToInt32(r["non_unique"]));
        Assert.Equal(0, indexes["seq"]);
        Assert.Contains("published_at,seq", indexes.Keys);
        Assert.Contains("message_group_id,published_at,seq", indexes.Keys);
    }

    [Fact]
    public async Task Outbox_payload_must_be_valid_json()
    {
        const string insert =
            "INSERT INTO outbox (id, event_type, schema_version, message_group_id, payload, created_at) " +
            "VALUES (@id, 'BookingCreated', 1, 'staff-1', @payload, @now)";
        await _sql.ExecuteAsync(insert, new { id = Guid.NewGuid(), payload = "{\"a\":1}", now = Sql.Now });
        await Assert.ThrowsAsync<MySqlException>(
            () => _sql.ExecuteAsync(insert, new { id = Guid.NewGuid(), payload = "not json", now = Sql.Now }));
    }

    // ------------------------------------------------------------ UTC (R2)

    [Fact]
    public async Task Timestamps_read_through_ef_are_utc() // R2
    {
        var parameters = Sql.BookingParameters(await _sql.InsertBookingFixtureAsync());
        await _sql.ExecuteAsync(Sql.InsertBookingSql, parameters);
        var id = (Guid)parameters["id"]!;

        await using var context = database.CreateContext();
        var booking = await context.Bookings.SingleAsync(b => b.Id == id);
        Assert.Equal(DateTimeKind.Utc, booking.StartUtc.Kind);
        Assert.Equal((DateTime)parameters["start"]!, booking.StartUtc);
    }
}
