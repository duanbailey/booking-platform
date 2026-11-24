using BookingPlatform.Api.Data;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace BookingPlatform.Api.Tests;

/// <summary>
/// T27a: ConnectionStrings:BookingDb keeps host, port and database; optional Database:User and Database:Password
/// override its credentials, so ECS can inject each secret on its own (decided 2026-11-24, plan/tasks.md).
/// </summary>
public class DatabaseConnectionTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();

    [Fact]
    public void Uses_the_connection_string_as_it_is_without_overrides()
    {
        var value = DatabaseConnection.FromConfiguration(Config(("ConnectionStrings:BookingDb", "Server=db;Port=3306;Database=booking;User ID=root;Password=pw;")));

        var built = new MySqlConnectionStringBuilder(value);
        Assert.Equal(("db", 3306u, "booking", "root", "pw"), (built.Server, built.Port, built.Database, built.UserID, built.Password));
    }

    [Fact]
    public void Database_User_and_Password_replace_the_credentials()
    {
        var value = DatabaseConnection.FromConfiguration(Config(
            ("ConnectionStrings:BookingDb", "Server=db;Port=3306;Database=booking;"),
            ("Database:User", "booking_api"),
            ("Database:Password", "p;w=\"d'")));

        var built = new MySqlConnectionStringBuilder(value);
        Assert.Equal(("db", "booking", "booking_api", "p;w=\"d'"), (built.Server, built.Database, built.UserID, built.Password));
    }

    [Fact]
    public void A_missing_connection_string_names_the_setting()
    {
        var error = Assert.Throws<InvalidOperationException>(() => DatabaseConnection.FromConfiguration(Config()));

        Assert.Contains("BookingDb", error.Message);
    }
}
