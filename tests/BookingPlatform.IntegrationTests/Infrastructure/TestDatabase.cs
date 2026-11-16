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
        await using var context = CreateContext();
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
