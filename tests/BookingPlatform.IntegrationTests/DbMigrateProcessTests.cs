using System.Diagnostics;
using BookingPlatform.IntegrationTests.Infrastructure;
using MySql.Data.MySqlClient;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T27a: `db migrate` through the real entry point, as the deploy script runs it in a one-off ECS task (decided
/// 2026-11-24, plan/tasks.md). Connected as the master user, it applies the migrations, then creates or updates the
/// API's user (data only: SELECT, INSERT, UPDATE, DELETE, no DDL) and the worker's user (exactly
/// worker/db/worker-grants.sql, specs/07-nfr.md), with passwords from settings. Rerunning is safe and rotates the
/// passwords; a grant removed from the file disappears. Exit codes as the admin script: 0 done, 2 usage, 3 failed.
/// Each test gets its own database and user names, dropped afterwards.
/// </summary>
public class DbMigrateProcessTests : IAsyncLifetime
{
    private const int TableAccessDenied = 1142, ColumnAccessDenied = 1143, LoginDenied = 1045;

    private static readonly string ApiDll = FindApiDll();
    private static readonly string GrantsFile = Path.Combine(RepoRoot(), "worker", "db", "worker-grants.sql");

    private readonly string _database = $"bp_migrate_{Guid.NewGuid():N}"[..28];
    private readonly string _apiUser = $"bpa_{Guid.NewGuid():N}"[..16];
    private readonly string _workerUser = $"bpw_{Guid.NewGuid():N}"[..16];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var connection = new MySqlConnection(TestDatabase.ServerConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[] { $"DROP DATABASE IF EXISTS `{_database}`", $"DROP USER IF EXISTS '{_apiUser}'@'%'", $"DROP USER IF EXISTS '{_workerUser}'@'%'" })
        {
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private string MasterConnection => new MySqlConnectionStringBuilder(TestDatabase.ServerConnectionString) { Database = _database }.ConnectionString;

    private Sql As(string user, string password) => new(new MySqlConnectionStringBuilder(TestDatabase.ServerConnectionString)
    {
        Database = _database, UserID = user, Password = password, Pooling = false,
    }.ConnectionString);

    private Sql Master => new(MasterConnection);

    private Dictionary<string, string?> Settings(string apiPassword = "api-password-1", string workerPassword = "worker-password-1") => new()
    {
        ["ConnectionStrings__BookingDb"] = MasterConnection,
        ["Db__ApiUser"] = _apiUser,
        ["Db__ApiPassword"] = apiPassword,
        ["Db__WorkerUser"] = _workerUser,
        ["Db__WorkerPassword"] = workerPassword,
        ["Db__WorkerGrantsFile"] = GrantsFile,
    };

    private static async Task<(int Exit, string Out, string Err)> RunAsync(Dictionary<string, string?> settings, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(ApiDll)!,
        };
        start.ArgumentList.Add(ApiDll);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        foreach (var (key, value) in settings) start.Environment[key] = value;

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }

    [Fact]
    public async Task Migrates_a_new_database_and_creates_both_users()
    {
        var (exit, output, error) = await RunAsync(Settings(), "db", "migrate");

        Assert.True(exit == 0, $"exit {exit}\n{output}\n{error}");
        Assert.Equal("20261124200105_InitialSchema",
            Convert.ToString(await Master.ScalarAsync("SELECT MigrationId FROM __EFMigrationsHistory")));
        Assert.Contains(_apiUser, output);
        Assert.Contains(_workerUser, output);
        Assert.DoesNotContain("api-password-1", output + error);
        Assert.DoesNotContain("worker-password-1", output + error);
    }

    [Fact]
    public async Task The_API_user_may_read_and_write_data_but_not_change_the_schema()
    {
        await RunAsync(Settings(), "db", "migrate");
        var api = As(_apiUser, "api-password-1");
        var id = Guid.NewGuid();

        await api.ExecuteAsync("INSERT INTO users (id, email, name, time_zone, created_at) VALUES (@id, @email, 'A', 'Europe/London', UTC_TIMESTAMP(6))",
            new { id, email = $"{id}@example.test" });
        await api.ExecuteAsync("UPDATE users SET name = 'B' WHERE id = @id", new { id });
        Assert.Equal("B", Convert.ToString(await api.ScalarAsync("SELECT name FROM users WHERE id = @id", new { id })));
        await api.ExecuteAsync("DELETE FROM users WHERE id = @id", new { id });

        Assert.Equal(TableAccessDenied, await api.FailsWithAsync("CREATE TABLE extra (id int)"));
        Assert.Equal(TableAccessDenied, await api.FailsWithAsync("DROP TABLE outbox"));
        Assert.Equal(TableAccessDenied, await api.FailsWithAsync("ALTER TABLE users ADD COLUMN extra int"));
    }

    [Fact]
    public async Task The_worker_user_has_exactly_the_grants_file()
    {
        await RunAsync(Settings(), "db", "migrate");
        var worker = As(_workerUser, "worker-password-1");

        await worker.ScalarAsync("SELECT COUNT(*) FROM users WHERE email = 'x'");
        Assert.Equal(ColumnAccessDenied, await worker.FailsWithAsync("SELECT password_hash FROM users"));
        Assert.Equal(TableAccessDenied, await worker.FailsWithAsync("SELECT * FROM refresh_tokens"));
    }

    [Fact]
    public async Task A_new_password_works_at_once_and_the_old_one_keeps_working_until_the_next_run()
    {
        await RunAsync(Settings(), "db", "migrate");

        // Rotation: tasks still running with the old password must keep connecting (MySQL dual passwords).
        var (exit, _, _) = await RunAsync(Settings(apiPassword: "api-password-2", workerPassword: "worker-password-2"), "db", "migrate");

        Assert.Equal(0, exit);
        Assert.Equal(1L, Convert.ToInt64(await Master.ScalarAsync("SELECT COUNT(*) FROM __EFMigrationsHistory")));
        await As(_apiUser, "api-password-2").ScalarAsync("SELECT 1");
        await As(_workerUser, "worker-password-2").ScalarAsync("SELECT 1");
        await As(_apiUser, "api-password-1").ScalarAsync("SELECT 1");
        await As(_workerUser, "worker-password-1").ScalarAsync("SELECT 1");

        // The next run (the following deploy, by when every task uses the new password) retires the old one.
        await RunAsync(Settings(apiPassword: "api-password-2", workerPassword: "worker-password-2"), "db", "migrate");

        Assert.Equal(LoginDenied, (await Assert.ThrowsAsync<MySqlException>(() => As(_apiUser, "api-password-1").ScalarAsync("SELECT 1"))).Number);
        Assert.Equal(LoginDenied, (await Assert.ThrowsAsync<MySqlException>(() => As(_workerUser, "worker-password-1").ScalarAsync("SELECT 1"))).Number);
        await As(_apiUser, "api-password-2").ScalarAsync("SELECT 1");
    }

    [Fact]
    public async Task Grants_that_already_match_are_left_alone_so_a_deploy_never_drops_live_privileges()
    {
        await RunAsync(Settings(), "db", "migrate");
        var grantedAt = await WorkerGrantTimestampAsync();
        await Task.Delay(TimeSpan.FromSeconds(1.5)); // mysql.tables_priv timestamps have one-second resolution

        var (exit, output, _) = await RunAsync(Settings(), "db", "migrate");

        Assert.Equal(0, exit);
        Assert.Contains($"User {_apiUser} already had exactly its grants", output);
        Assert.Contains($"User {_workerUser} already had exactly its grants", output);
        Assert.Equal(grantedAt, await WorkerGrantTimestampAsync()); // no REVOKE and re-GRANT happened
    }

    private async Task<DateTime> WorkerGrantTimestampAsync() => Convert.ToDateTime(await Master.ScalarAsync(
        "SELECT MAX(Timestamp) FROM mysql.tables_priv WHERE User = @user", new { user = _workerUser }));

    [Fact]
    public async Task A_grant_that_is_not_in_the_file_is_removed_on_the_next_run()
    {
        await RunAsync(Settings(), "db", "migrate");
        await Master.ExecuteAsync($"GRANT SELECT ON `{_database}`.outbox TO '{_workerUser}'@'%'");
        await As(_workerUser, "worker-password-1").ScalarAsync("SELECT COUNT(*) FROM outbox");

        var (_, output, _) = await RunAsync(Settings(), "db", "migrate");

        Assert.Equal(TableAccessDenied, await As(_workerUser, "worker-password-1").FailsWithAsync("SELECT COUNT(*) FROM outbox"));
        Assert.Contains($"User {_workerUser} grants updated", output);
        await As(_workerUser, "worker-password-1").ScalarAsync("SELECT COUNT(*) FROM users WHERE email = 'x'"); // the file's grants are back
    }

    [Theory]
    [InlineData("Db__ApiPassword")]
    [InlineData("Db__WorkerPassword")]
    public async Task A_missing_password_is_a_usage_error_that_names_the_setting_and_changes_nothing(string setting)
    {
        var settings = Settings();
        settings[setting] = null;

        var (exit, _, error) = await RunAsync(settings, "db", "migrate");

        Assert.Equal(2, exit);
        Assert.Contains(setting.Replace("__", ":"), error);
        Assert.Equal(0L, Convert.ToInt64(await new Sql(TestDatabase.ServerConnectionString).ScalarAsync(
            "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @name", new { name = _database })));
    }

    [Fact]
    public async Task A_user_name_that_is_not_a_plain_identifier_is_refused()
    {
        var settings = Settings();
        settings["Db__ApiUser"] = "x'@'%' WITH GRANT OPTION; --";

        var (exit, _, error) = await RunAsync(settings, "db", "migrate");

        Assert.Equal(2, exit);
        Assert.Contains("Db:ApiUser", error);
    }

    [Fact]
    public async Task An_unknown_db_command_prints_usage()
    {
        var (exit, _, error) = await RunAsync(Settings(), "db", "frobnicate");

        Assert.Equal(2, exit);
        Assert.Contains("db migrate", error);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BookingPlatform.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root (BookingPlatform.slnx) not found.");
    }

    private static string FindApiDll()
    {
        var testBin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var dll = Path.Combine(RepoRoot(), "src", "BookingPlatform.Api", "bin", testBin.Parent!.Name, testBin.Name, "BookingPlatform.Api.dll");
        return File.Exists(dll) ? dll : throw new FileNotFoundException("Build the API first.", dll);
    }
}
