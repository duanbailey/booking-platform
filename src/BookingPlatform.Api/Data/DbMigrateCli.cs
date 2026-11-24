using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

namespace BookingPlatform.Api.Data;

/// <summary>
/// <c>db migrate</c> (T27a, decided 2026-11-24): run by the deploy script as a one-off ECS task from the API image,
/// connected as the RDS master user. Applies the EF Core migrations (ADR 0006), then creates or updates two database
/// users at host '%': the API's (data only: SELECT, INSERT, UPDATE, DELETE on the database, no DDL) and the worker's
/// (exactly <c>worker/db/worker-grants.sql</c>, specs/07-nfr.md). A user whose privileges already match is left alone,
/// so a deploy never drops privileges that live tasks are using; one that differs (e.g. a grant removed from the file)
/// is revoked and granted again. Passwords are set on every run with MySQL's RETAIN CURRENT PASSWORD, so after a
/// rotation tasks still holding the previous password keep connecting until the next run retires it (T27a review).
/// Settings (section "Db"): ApiUser, ApiPassword, WorkerUser, WorkerPassword, WorkerGrantsFile.
/// Exit codes as the admin script: 0 done, 2 usage error (nothing changed), 3 failed unexpectedly.
/// Passwords are never printed or logged.
/// </summary>
public static partial class DbMigrateCli
{
    public const int Ok = 0, UsageError = 2, Failed = 3;

    public const string Usage = """
        Usage:
          db migrate    Apply migrations, then create or update the API and worker database users.
        Settings: ConnectionStrings:BookingDb (the master user, or Database:User and Database:Password),
        Db:ApiUser (default booking_api), Db:ApiPassword, Db:WorkerUser (default booking_worker), Db:WorkerPassword,
        Db:WorkerGrantsFile (default db/worker-grants.sql next to the binary).
        """;

    [GeneratedRegex("^[A-Za-z0-9_]{1,32}$")]
    private static partial Regex UserName();

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$")]
    private static partial Regex DatabaseName();

    private sealed record Settings(string ConnectionString, string Database, string ApiUser, string ApiPassword,
        string WorkerUser, string WorkerPassword, string GrantsFile);

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, ILogger logger, TextWriter output, TextWriter error)
    {
        if (args is not ["migrate"])
        {
            await error.WriteLineAsync(Usage);
            return UsageError;
        }

        Settings settings;
        try
        {
            settings = Read(configuration);
        }
        catch (ArgumentException e)
        {
            await error.WriteLineAsync(e.Message);
            await error.WriteLineAsync(Usage);
            return UsageError;
        }

        try
        {
            var migration = await MigrateAsync(settings.ConnectionString);
            await output.WriteLineAsync($"Database {settings.Database} is migrated to {migration}.");

            await using var connection = new MySqlConnection(settings.ConnectionString);
            await connection.OpenAsync();
            var api = await SetUserAsync(connection, settings.ApiUser, settings.ApiPassword,
                user => [$"GRANT SELECT, INSERT, UPDATE, DELETE ON `{settings.Database}`.* TO '{user}'@'%'"]);
            await output.WriteLineAsync(Describe(settings.ApiUser, api, "data only: SELECT, INSERT, UPDATE, DELETE; no schema changes"));
            var grantsSql = await File.ReadAllTextAsync(settings.GrantsFile);
            var worker = await SetUserAsync(connection, settings.WorkerUser, settings.WorkerPassword,
                user => GrantStatements(grantsSql, settings.Database, user));
            await output.WriteLineAsync(Describe(settings.WorkerUser, worker, Path.GetFileName(settings.GrantsFile)));
            return Ok;
        }
        catch (Exception e)
        {
            logger.LogError(e, "db migrate failed");
            await error.WriteLineAsync($"Failed: {e.Message.ReplaceLineEndings(" ")}");
            return Failed;
        }
    }

    private static Settings Read(IConfiguration configuration)
    {
        string connectionString;
        try
        {
            connectionString = DatabaseConnection.FromConfiguration(configuration);
        }
        catch (InvalidOperationException e)
        {
            throw new ArgumentException(e.Message);
        }
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        if (!DatabaseName().IsMatch(database ?? ""))
            throw new ArgumentException("The connection string must name a database of letters, digits and underscores.");

        var db = configuration.GetSection("Db");
        string User(string key, string fallback)
        {
            var value = db[key] is { Length: > 0 } v ? v : fallback;
            return UserName().IsMatch(value) ? value : throw new ArgumentException($"Db:{key} must be 1-32 letters, digits or underscores.");
        }
        string Password(string key) => db[key] is { Length: > 0 } v ? v : throw new ArgumentException($"Db:{key} is required.");

        var apiUser = User("ApiUser", "booking_api");
        var workerUser = User("WorkerUser", "booking_worker");
        if (apiUser == workerUser) throw new ArgumentException("Db:ApiUser and Db:WorkerUser must differ.");
        var apiPassword = Password("ApiPassword");
        var workerPassword = Password("WorkerPassword");
        var grantsFile = db["WorkerGrantsFile"] is { Length: > 0 } f ? f : Path.Combine(AppContext.BaseDirectory, "db", "worker-grants.sql");
        if (!File.Exists(grantsFile)) throw new ArgumentException($"Db:WorkerGrantsFile not found: {grantsFile}");
        return new(connectionString, database!, apiUser, apiPassword, workerUser, workerPassword, grantsFile);
    }

    /// <summary>Applies pending migrations (creating the database if needed) and returns the latest one.</summary>
    private static async Task<string> MigrateAsync(string connectionString)
    {
        // Unpooled: EF's MySQL migrations hold a session-level named lock, released when the session ends.
        var unpooled = new MySqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        await using var context = new BookingDbContext(new DbContextOptionsBuilder<BookingDbContext>().UseMySQL(unpooled).Options);
        await context.Database.MigrateAsync();
        return (await context.Database.GetAppliedMigrationsAsync()).Last();
    }

    private static string Describe(string user, bool changed, string what) => changed
        ? $"User {user} grants updated ({what})."
        : $"User {user} already had exactly its grants ({what}).";

    /// <summary>
    /// Creates the user or sets its password, then makes its privileges exactly what <paramref name="grantsFor"/>
    /// grants, touching them only if they differ. Returns whether they changed.
    /// </summary>
    private static async Task<bool> SetUserAsync(MySqlConnection connection, string user, string password, Func<string, IEnumerable<string>> grantsFor)
    {
        // User names are checked against UserName(); the password goes as a parameter.
        await ExecuteAsync(connection, $"CREATE USER IF NOT EXISTS '{user}'@'%' IDENTIFIED BY @password", password);
        await ExecuteAsync(connection, $"ALTER USER '{user}'@'%' IDENTIFIED BY @password RETAIN CURRENT PASSWORD", password);

        if ((await DesiredPrivilegesAsync(connection, grantsFor)).SetEquals(await PrivilegesAsync(connection, user))) return false;

        // REVOKE and GRANT aren't atomic in MySQL, so the user has no privileges for a moment: only when they change.
        await ExecuteAsync(connection, $"REVOKE ALL PRIVILEGES, GRANT OPTION FROM '{user}'@'%'");
        foreach (var grant in grantsFor(user)) await ExecuteAsync(connection, grant);
        return true;
    }

    /// <summary>The privileges the grants give, read from a locked probe user they are applied to and then dropped.</summary>
    private static async Task<HashSet<string>> DesiredPrivilegesAsync(MySqlConnection connection, Func<string, IEnumerable<string>> grantsFor)
    {
        var probe = $"dbm_probe_{Guid.NewGuid():N}"[..32];
        await ExecuteAsync(connection, $"CREATE USER '{probe}'@'%' ACCOUNT LOCK");
        try
        {
            foreach (var grant in grantsFor(probe)) await ExecuteAsync(connection, grant);
            return await PrivilegesAsync(connection, probe);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP USER IF EXISTS '{probe}'@'%'");
        }
    }

    /// <summary>Every global, database, table and column privilege of the user, as comparable strings.</summary>
    private static async Task<HashSet<string>> PrivilegesAsync(MySqlConnection connection, string user)
    {
        const string sql =
            "SELECT CONCAT('global|', privilege_type, '|', is_grantable) FROM information_schema.user_privileges WHERE grantee = @grantee " +
            "UNION ALL SELECT CONCAT('schema|', table_schema, '|', privilege_type, '|', is_grantable) FROM information_schema.schema_privileges WHERE grantee = @grantee " +
            "UNION ALL SELECT CONCAT('table|', table_schema, '|', table_name, '|', privilege_type, '|', is_grantable) FROM information_schema.table_privileges WHERE grantee = @grantee " +
            "UNION ALL SELECT CONCAT('column|', table_schema, '|', table_name, '|', column_name, '|', privilege_type, '|', is_grantable) FROM information_schema.column_privileges WHERE grantee = @grantee";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@grantee", $"'{user}'@'%'");
        var privileges = new HashSet<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) privileges.Add(reader.GetString(0));
        return privileges;
    }

    /// <summary>The grants file's statements, with its placeholders filled in and comments dropped.</summary>
    public static IEnumerable<string> GrantStatements(string grantsSql, string database, string user)
    {
        var withoutComments = string.Join('\n', grantsSql.Split('\n').Where(line => !line.TrimStart().StartsWith("--")));
        return withoutComments
            .Replace("{{database}}", database)
            .Replace("{{user}}", $"'{user}'@'%'")
            .Split(';')
            .Select(statement => statement.Trim())
            .Where(statement => statement.Length > 0);
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql, string? password = null)
    {
        await using var command = new MySqlCommand(sql, connection);
        if (password is not null) command.Parameters.AddWithValue("@password", password);
        await command.ExecuteNonQueryAsync();
    }
}
