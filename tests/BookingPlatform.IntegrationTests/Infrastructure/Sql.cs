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

    /// <summary>
    /// Waits until some statement is blocked on a row lock in <paramref name="table"/> and
    /// returns its SQL. Uses performance_schema.data_lock_waits, not innodb_trx, which is a
    /// cache refreshed only after 0.1 s without reads (see RefreshTokenTests).
    /// </summary>
    public async Task<string> WaitForLockWaitAsync(string table)
    {
        const string waitingStatement =
            "SELECT s.SQL_TEXT FROM performance_schema.data_lock_waits w " +
            "JOIN performance_schema.data_locks l ON l.ENGINE_LOCK_ID = w.REQUESTING_ENGINE_LOCK_ID " +
            "JOIN performance_schema.events_statements_current s ON s.THREAD_ID = w.REQUESTING_THREAD_ID " +
            "WHERE l.OBJECT_SCHEMA = DATABASE() AND l.OBJECT_NAME = @table LIMIT 1";

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await ScalarAsync(waitingStatement, new { table }) is string sql) return sql;
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException($"Nothing waited for a row lock on {table}.");
    }

    /// <summary>
    /// Opens a transaction holding <c>SELECT ... FOR UPDATE</c> on one user row. It is rolled
    /// back on dispose unless committed; statements can run inside it meanwhile.
    /// </summary>
    public async Task<HeldLock> LockUserRowAsync(Guid userId)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var held = new HeldLock(connection, await connection.BeginTransactionAsync());
        await held.ExecuteAsync("SELECT id FROM users WHERE id = @userId FOR UPDATE", new { userId });
        return held;
    }

    public sealed class HeldLock(MySqlConnection connection, MySqlTransaction transaction) : IAsyncDisposable
    {
        private bool _committed;

        public async Task ExecuteAsync(string sql, object? parameters = null)
        {
            await using var command = new MySqlCommand(sql, connection, transaction);
            AddParameters(command, parameters);
            await command.ExecuteNonQueryAsync();
        }

        public async Task CommitAsync()
        {
            await transaction.CommitAsync();
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed) await transaction.RollbackAsync();
            await connection.DisposeAsync();
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

    /// <summary>
    /// Inserts a user_invites row. With a <paramref name="token"/>, it is stored as the worker
    /// would store it (T26): its lowercase hex SHA-256 and an expiry; without one, as the API
    /// leaves it before the worker has sent the email.
    /// </summary>
    public async Task<Guid> InsertInviteAsync(Guid userId, string? token = null, DateTime? expiresAt = null,
        DateTime? createdAt = null, DateTime? usedAt = null, Guid? id = null, Guid? createdBy = null)
    {
        var inviteId = id ?? Guid.NewGuid();
        await ExecuteAsync(
            "INSERT INTO user_invites (id, user_id, token_hash, expires_at, used_at, created_by, created_at) " +
            "VALUES (@inviteId, @userId, @tokenHash, @expiresAt, @usedAt, @createdBy, @createdAt)",
            new
            {
                inviteId, userId, tokenHash = token is null ? null : TokenHash(token),
                expiresAt = token is null ? (DateTime?)null : expiresAt ?? DateTime.UtcNow.AddDays(7),
                usedAt, createdBy = createdBy ?? userId, createdAt = createdAt ?? Now,
            });
        return inviteId;
    }

    public static string TokenHash(string token) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

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
