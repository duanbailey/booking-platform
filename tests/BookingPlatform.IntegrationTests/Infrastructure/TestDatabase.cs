using BookingPlatform.Api.Data;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

namespace BookingPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// A throwaway MySQL database for one test class: created with a unique name,
/// migrated to the latest schema, and dropped on dispose.
/// Server comes from BOOKING_TEST_MYSQL (defaults to docker-compose.yml's MySQL).
/// </summary>
public sealed class TestDatabase : IAsyncLifetime
{
    public const string DefaultServerConnectionString =
        "Server=localhost;Port=3307;User ID=root;Password=localdev;";

    public static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("BOOKING_TEST_MYSQL") ?? DefaultServerConnectionString;

    public string Name { get; } = $"bp_test_{Guid.NewGuid():N}";

    public string ConnectionString => new MySqlConnectionStringBuilder(ServerConnectionString)
    {
        Database = Name,
    }.ConnectionString;

    public BookingDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<BookingDbContext>().UseMySQL(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        // Without pooling: EF Core's MySQL migrations take the server-wide named lock
        // GET_LOCK('__EFMigrationsLock'), which belongs to the session. A pooled connection
        // could go back to the pool still holding it, and every other test class's migration
        // would then wait for it forever (seen 2026-11-18). Closing an unpooled connection
        // ends the session, which always releases the lock.
        var unpooled = new MySqlConnectionStringBuilder(ConnectionString) { Pooling = false }.ConnectionString;
        await using var context = new BookingDbContext(
            new DbContextOptionsBuilder<BookingDbContext>().UseMySQL(unpooled).Options);
        // Migrate() creates the database if it does not exist.
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var connection = new MySqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand($"DROP DATABASE IF EXISTS `{Name}`", connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<bool> ExistsAsync(string name)
    {
        await using var connection = new MySqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @name", connection);
        command.Parameters.AddWithValue("@name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }
}
