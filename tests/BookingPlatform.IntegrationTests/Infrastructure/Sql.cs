using MySql.Data.MySqlClient;

namespace BookingPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// Raw SQL helpers for schema tests. They use the column names from
/// specs/05-data-model.md directly, so the tests check the real database
/// constraints rather than the EF Core model (ADR 0006).
/// </summary>
public sealed class Sql(string connectionString)
{
    public static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    public async Task<int> ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        AddParameters(command, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarAsync(string sql, object? parameters = null)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        AddParameters(command, parameters);
        return await command.ExecuteScalarAsync();
    }

    /// <summary>Runs the statement and returns the MySQL error number it fails with.</summary>
    public async Task<int> FailsWithAsync(string sql, object? parameters = null)
    {
        var exception = await Assert.ThrowsAsync<MySqlException>(() => ExecuteAsync(sql, parameters));
        return exception.Number;
    }

    private static void AddParameters(MySqlCommand command, object? parameters)
    {
        if (parameters is null) return;
        foreach (var property in parameters.GetType().GetProperties())
        {
            command.Parameters.AddWithValue("@" + property.Name, property.GetValue(parameters) ?? DBNull.Value);
        }
    }

    // ------------------------------------------------------------ Seed rows

    /// <summary>Inserts a user; with no <paramref name="passwordHash"/> the account has no password (invited).</summary>
    public async Task<Guid> InsertUserAsync(string? email = null, string? passwordHash = null)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            "INSERT INTO users (id, email, password_hash, name, time_zone, created_at) " +
            "VALUES (@id, @email, @passwordHash, 'Test User', 'Europe/London', @now)",
            new { id, email = email ?? $"{id:N}@example.test", passwordHash, now = Now });
        return id;
    }

    public async Task InsertAdminAsync(Guid userId, Guid? grantedBy = null) =>
        await ExecuteAsync(
            "INSERT INTO platform_admins (user_id, granted_by, granted_at) VALUES (@userId, @grantedBy, @now)",
            new { userId, grantedBy, now = Now });

    /// <summary>A user who is a platform admin, granted by the bootstrap admin of this database.</summary>
    public async Task<Guid> InsertAdminUserAsync()
    {
        var id = await InsertUserAsync(passwordHash: "not-a-real-hash");
        await InsertAdminAsync(id);
        return id;
    }

    /// <summary>Inserts a business with only the required columns, so settings take their defaults.</summary>
    public async Task<Guid> InsertBusinessAsync(Guid? ownerUserId = null)
    {
        var owner = ownerUserId ?? await InsertUserAsync();
        var id = Guid.NewGuid();
        await ExecuteAsync(
            "INSERT INTO businesses (id, name, owner_user_id, time_zone, created_at) " +
            "VALUES (@id, 'Test Business', @owner, 'Europe/London', @now)",
            new { id, owner, now = Now });
        return id;
    }

    public async Task<Guid> InsertStaffAsync(Guid businessId, Guid? userId = null)
    {
        var user = userId ?? await InsertUserAsync();
        var id = Guid.NewGuid();
        await ExecuteAsync(
            "INSERT INTO staff (id, user_id, business_id, removed_at, created_at) " +
            "VALUES (@id, @user, @businessId, NULL, @now)",
            new { id, user, businessId, now = Now });
        return id;
    }

    public async Task<Guid> InsertServiceAsync(Guid businessId)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            "INSERT INTO services (id, business_id, name, duration_minutes, buffer_before_minutes, " +
            "buffer_after_minutes, description, price, created_at) " +
            "VALUES (@id, @businessId, 'Haircut', 60, 15, 15, NULL, 45.00, @now)",
            new { id, businessId, now = Now });
        return id;
    }

    public Task RemoveStaffAsync(Guid staffId) =>
        ExecuteAsync("UPDATE staff SET removed_at = @now WHERE id = @staffId", new { staffId, now = Now });

    /// <summary>Grants admin as the bootstrap admin (granted_by null).</summary>
    public Task InsertAdminAsync(Guid userId) =>
        ExecuteAsync("INSERT INTO platform_admins (user_id, granted_by, granted_at) VALUES (@userId, NULL, @now)",
            new { userId, now = Now });

    /// <summary>Inserts a Confirmed booking with <see cref="BookingParameters"/> and returns its ID.</summary>
    public async Task<Guid> InsertBookingAsync(BookingFixture fixture)
    {
        var parameters = BookingParameters(fixture);
        await ExecuteAsync(InsertBookingSql, parameters);
        return (Guid)parameters["id"]!;
    }

    public sealed record BookingFixture(Guid BusinessId, Guid StaffId, Guid ServiceId, Guid CustomerId);

    public async Task<BookingFixture> InsertBookingFixtureAsync()
    {
        var business = await InsertBusinessAsync();
        return new BookingFixture(
            business, await InsertStaffAsync(business), await InsertServiceAsync(business), await InsertUserAsync());
    }

    public const string InsertBookingSql =
        "INSERT INTO bookings (id, staff_id, service_id, customer_id, start_utc, end_utc, " +
        "blocked_start_utc, blocked_end_utc, status, created_at, cancelled_at, cancelled_by, " +
        "cancellation_reason, version) VALUES (@id, @staff, @service, @customer, @start, @end, " +
        "@blockedStart, @blockedEnd, @status, @now, @cancelledAt, @cancelledBy, @reason, 1)";

    /// <summary>Parameters for a valid Confirmed 10:00-11:00 booking with 15-minute buffers; override as needed.</summary>
    public static Dictionary<string, object?> BookingParameters(BookingFixture f) => new()
    {
        ["id"] = Guid.NewGuid(),
        ["staff"] = f.StaffId,
        ["service"] = f.ServiceId,
        ["customer"] = f.CustomerId,
        ["start"] = Now.AddDays(1).Date.AddHours(10),
        ["end"] = Now.AddDays(1).Date.AddHours(11),
        ["blockedStart"] = Now.AddDays(1).Date.AddHours(9.75),
        ["blockedEnd"] = Now.AddDays(1).Date.AddHours(11.25),
        ["status"] = "Confirmed",
        ["now"] = Now,
        ["cancelledAt"] = null,
        ["cancelledBy"] = null,
        ["reason"] = null,
    };

    public async Task<int> ExecuteAsync(string sql, Dictionary<string, object?> parameters)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue("@" + name, value ?? DBNull.Value);
        }
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<int> FailsWithAsync(string sql, Dictionary<string, object?> parameters)
    {
        var exception = await Assert.ThrowsAsync<MySqlException>(() => ExecuteAsync(sql, parameters));
        return exception.Number;
    }
}

/// <summary>MySQL server error numbers the schema tests expect.</summary>
public static class ExpectedError
{
    public const int DuplicateKey = 1062;
    public const int ColumnCannotBeNull = 1048;
    public const int ForeignKeyViolation = 1452;
    public const int DataTruncated = 1265; // e.g. a value outside an ENUM, in strict mode
    public const int CheckViolation = 3819;
}
