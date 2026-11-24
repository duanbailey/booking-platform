using MySql.Data.MySqlClient;

namespace BookingPlatform.Api.Data;

/// <summary>
/// The MySQL connection string (T27a, decided 2026-11-24): ConnectionStrings:BookingDb gives host, port and database,
/// and optional Database:User and Database:Password replace its credentials, so ECS can inject each secret on its own
/// (the RDS-managed master secret and the generated user passwords) without building a connection string.
/// </summary>
public static class DatabaseConnection
{
    public const string ConnectionStringName = "BookingDb";

    public static string FromConfiguration(IConfiguration configuration)
    {
        var builder = new MySqlConnectionStringBuilder(configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured."));
        if (configuration["Database:User"] is { Length: > 0 } user) builder.UserID = user;
        if (configuration["Database:Password"] is { } password) builder.Password = password;
        return builder.ConnectionString;
    }
}
