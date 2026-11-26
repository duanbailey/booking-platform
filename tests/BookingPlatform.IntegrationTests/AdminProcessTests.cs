using System.Diagnostics;
using BookingPlatform.IntegrationTests.Infrastructure;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T6: the admin script through the real entry point (Program.cs), as an operator runs it:
/// `dotnet BookingPlatform.Api.dll admin ...`. Checks the arguments reach the script, stdout
/// holds only the script's output, and logs (including the grant/revoke audit lines) go to
/// stderr. Behaviour of each command is in AdminCliTests.
/// </summary>
public class AdminProcessTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static readonly string ApiDll = FindApiDll();

    private static async Task<(int Exit, string Out, string Err)> RunAsync(string connectionString, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(ApiDll)!,
        };
        start.ArgumentList.Add(ApiDll);
        start.ArgumentList.Add("admin");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        // Development logs EF Core commands at Information, so a leak onto stdout would show.
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__BookingDb"] = connectionString;

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }

    [Fact]
    public async Task Grant_and_revoke_print_one_line_each_on_stdout_and_audit_lines_on_stderr()
    {
        await fixture.Sql.ExecuteAsync("DELETE FROM platform_admins");
        var firstEmail = ApiFixture.NewEmail();
        var first = await fixture.Sql.InsertUserAsync(firstEmail, passwordHash: "not-a-real-hash");
        var secondEmail = ApiFixture.NewEmail();
        var second = await fixture.Sql.InsertUserAsync(secondEmail, passwordHash: "not-a-real-hash");
        var db = fixture.Database.ConnectionString;

        var bootstrap = await RunAsync(db, "grant", firstEmail);
        var grant = await RunAsync(db, "grant", secondEmail, "--granted-by", firstEmail);
        var revoke = await RunAsync(db, "revoke", secondEmail);

        Assert.Equal((0, $"{firstEmail} is now an admin (bootstrap).{Environment.NewLine}"), (bootstrap.Exit, bootstrap.Out));
        Assert.Equal((0, $"{secondEmail} is now an admin, granted by {firstEmail}.{Environment.NewLine}"), (grant.Exit, grant.Out));
        Assert.Equal((0, $"{secondEmail} is no longer an admin.{Environment.NewLine}"), (revoke.Exit, revoke.Out));

        // Audit lines: JSON logs on stderr, naming users by ID only (no emails in logs).
        Assert.Contains("Admin role granted", bootstrap.Err);
        Assert.Contains(first.ToString(), bootstrap.Err);
        Assert.Contains("Admin role granted", grant.Err);
        Assert.Contains(second.ToString(), grant.Err);
        Assert.Contains(first.ToString(), grant.Err);
        Assert.Contains("Admin role revoked", revoke.Err);
        Assert.Contains(second.ToString(), revoke.Err);
        foreach (var err in new[] { bootstrap.Err, grant.Err, revoke.Err })
        {
            Assert.DoesNotContain(firstEmail, err);
            Assert.DoesNotContain(secondEmail, err);
            Assert.DoesNotContain("Executed DbCommand", err); // EF Information logs are off
        }
    }

    [Fact]
    public async Task No_command_prints_usage_on_stderr_and_exits_2()
    {
        var (exit, output, error) = await RunAsync(fixture.Database.ConnectionString);

        Assert.Equal(2, exit);
        Assert.Empty(output);
        Assert.Contains("Usage", error);
    }

    [Fact]
    public async Task Options_are_read_by_the_script_not_the_host()
    {
        // --granted-by must reach the script: here it names an unknown account, so the grant is refused.
        var email = ApiFixture.NewEmail();
        await fixture.Sql.InsertUserAsync(email, passwordHash: "not-a-real-hash");
        var unknown = ApiFixture.NewEmail();

        var (exit, output, error) = await RunAsync(fixture.Database.ConnectionString, "grant", email, "--granted-by", unknown);

        Assert.Equal(1, exit);
        Assert.Empty(output);
        Assert.Contains($"No account has the email {unknown}.", error);
    }

    [Fact]
    public async Task Unreachable_database_exits_3_with_nothing_on_stdout()
    {
        var (exit, output, error) = await RunAsync(AdminCliTests.UnreachableDatabase, "list");

        Assert.Equal(3, exit);
        Assert.Empty(output);
        Assert.Contains("Failed:", error);
    }

    /// <summary>The API build matching this test build (same configuration and framework).</summary>
    internal static string FindApiDll()
    {
        // .../tests/BookingPlatform.IntegrationTests/bin/<Configuration>/<tfm>/
        var testBin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var tfm = testBin.Name;
        var configuration = testBin.Parent!.Name;
        var repo = testBin.Parent.Parent!.Parent!.Parent!.Parent!.FullName;
        var dll = Path.Combine(repo, "src", "BookingPlatform.Api", "bin", configuration, tfm, "BookingPlatform.Api.dll");
        return File.Exists(dll) ? dll : throw new FileNotFoundException("Build the API first.", dll);
    }
}
