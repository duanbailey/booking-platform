using BookingPlatform.Api.Admin;
using BookingPlatform.Api.Data;
using BookingPlatform.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T6: the operator script that grants and revokes admin (specs/05-data-model.md
/// platform_admins): `admin grant|revoke|list` in the API binary. Exit codes: 0 done or
/// nothing to change, 1 refused, 2 usage error. Each test starts with no admins.
/// </summary>
public class AdminCliTests(ApiFixture fixture) : IClassFixture<ApiFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.Sql.ExecuteAsync("DELETE FROM platform_admins");

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await AdminCli.RunAsync(args, fixture.Api.Services, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<(Guid Id, string Email)> UserAsync()
    {
        var email = ApiFixture.NewEmail();
        return (await fixture.Sql.InsertUserAsync(email, passwordHash: "not-a-real-hash"), email);
    }

    private async Task<long> AdminCountAsync() =>
        Convert.ToInt64(await fixture.Sql.ScalarAsync("SELECT COUNT(*) FROM platform_admins"));

    private async Task<string?> GrantedByAsync(Guid userId) =>
        await fixture.Sql.ScalarAsync("SELECT granted_by FROM platform_admins WHERE user_id = @userId", new { userId })
            is { } value and not DBNull ? Convert.ToString(value) : null;

    private async Task<bool> IsAdminAsync(Guid userId) => Convert.ToInt64(await fixture.Sql.ScalarAsync(
        "SELECT COUNT(*) FROM platform_admins WHERE user_id = @userId", new { userId })) == 1;

    [Fact]
    public async Task Bootstrap_grant_without_a_granter_works_while_there_are_no_admins()
    {
        var (user, email) = await UserAsync();

        var (exit, output, _) = await RunAsync("grant", email);

        Assert.Equal(0, exit);
        Assert.True(await IsAdminAsync(user));
        Assert.Null(await GrantedByAsync(user)); // only the bootstrap admin has granted_by null
        Assert.Contains(email, output);
    }

    [Fact]
    public async Task Bootstrap_grant_is_refused_once_an_admin_exists()
    {
        var (first, firstEmail) = await UserAsync();
        await RunAsync("grant", firstEmail);
        var (second, secondEmail) = await UserAsync();

        var (exit, _, error) = await RunAsync("grant", secondEmail);

        Assert.Equal(1, exit);
        Assert.False(await IsAdminAsync(second));
        Assert.Contains("--granted-by", error);
        Assert.True(await IsAdminAsync(first));
    }

    [Fact]
    public async Task Grant_names_an_existing_admin_as_granted_by()
    {
        var (admin, adminEmail) = await UserAsync();
        await RunAsync("grant", adminEmail);
        var (user, email) = await UserAsync();

        var (exit, _, _) = await RunAsync("grant", email, "--granted-by", adminEmail);

        Assert.Equal(0, exit);
        Assert.Equal(admin.ToString(), await GrantedByAsync(user));
    }

    [Fact]
    public async Task Grant_by_someone_who_is_not_an_admin_is_refused()
    {
        var (admin, adminEmail) = await UserAsync();
        await RunAsync("grant", adminEmail);
        var (_, notAdminEmail) = await UserAsync();
        var (user, email) = await UserAsync();

        var (exit, _, error) = await RunAsync("grant", email, "--granted-by", notAdminEmail);

        Assert.Equal(1, exit);
        Assert.False(await IsAdminAsync(user));
        Assert.Contains(notAdminEmail, error);
    }

    [Fact]
    public async Task Granter_is_checked_even_when_there_are_no_admins()
    {
        var (_, notAdminEmail) = await UserAsync();
        var (user, email) = await UserAsync();

        var (exit, _, _) = await RunAsync("grant", email, "--granted-by", notAdminEmail);

        Assert.Equal(1, exit);
        Assert.Equal(0, await AdminCountAsync());
    }

    [Theory]
    [InlineData("grantee")]
    [InlineData("granter")]
    public async Task Unknown_email_is_refused(string which)
    {
        var (_, adminEmail) = await UserAsync();
        await RunAsync("grant", adminEmail);
        var (_, email) = await UserAsync();
        var unknown = ApiFixture.NewEmail();

        var (exit, _, error) = which == "grantee"
            ? await RunAsync("grant", unknown, "--granted-by", adminEmail)
            : await RunAsync("grant", email, "--granted-by", unknown);

        Assert.Equal(1, exit);
        Assert.Contains(unknown, error);
        Assert.Equal(1, await AdminCountAsync());
    }

    [Fact]
    public async Task Granting_an_existing_admin_changes_nothing()
    {
        var (admin, adminEmail) = await UserAsync();
        await RunAsync("grant", adminEmail);
        var grantedAt = await fixture.Sql.ScalarAsync("SELECT granted_at FROM platform_admins WHERE user_id = @admin", new { admin });

        var (exit, output, _) = await RunAsync("grant", adminEmail, "--granted-by", adminEmail);

        Assert.Equal(0, exit);
        Assert.Contains("already", output);
        Assert.Null(await GrantedByAsync(admin));
        Assert.Equal(grantedAt, await fixture.Sql.ScalarAsync(
            "SELECT granted_at FROM platform_admins WHERE user_id = @admin", new { admin }));
    }

    [Fact]
    public async Task Emails_are_matched_ignoring_case_and_surrounding_spaces()
    {
        var (user, email) = await UserAsync();

        var (exit, _, _) = await RunAsync("grant", $" {email.ToUpperInvariant()} ");

        Assert.Equal(0, exit);
        Assert.True(await IsAdminAsync(user));
    }

    [Fact]
    public async Task Revoke_removes_the_role()
    {
        var (first, firstEmail) = await UserAsync();
        await RunAsync("grant", firstEmail);
        var (second, secondEmail) = await UserAsync();
        await RunAsync("grant", secondEmail, "--granted-by", firstEmail);

        var (exit, _, _) = await RunAsync("revoke", secondEmail);

        Assert.Equal(0, exit);
        Assert.False(await IsAdminAsync(second));
        // The revoked user can no longer create owners.
        await Json.ErrorAsync(await fixture.Api.ClientFor(second).PostJsonAsync("/v1/admin/owners",
                new { ownerName = "O", ownerEmail = ApiFixture.NewEmail(), businessName = "B", businessTimeZone = "Europe/London" }),
            System.Net.HttpStatusCode.Forbidden, "Forbidden");
    }

    [Fact]
    public async Task Admin_granted_by_a_revoked_admin_keeps_the_role()
    {
        var (first, firstEmail) = await UserAsync();
        await RunAsync("grant", firstEmail);
        var (second, secondEmail) = await UserAsync();
        await RunAsync("grant", secondEmail, "--granted-by", firstEmail);

        Assert.Equal(0, (await RunAsync("revoke", firstEmail)).Exit);

        Assert.True(await IsAdminAsync(second));
        Assert.Equal(first.ToString(), await GrantedByAsync(second)); // history kept
    }

    [Fact]
    public async Task Revoking_the_last_admin_is_refused()
    {
        var (admin, adminEmail) = await UserAsync();
        await RunAsync("grant", adminEmail);

        var (exit, _, error) = await RunAsync("revoke", adminEmail);

        Assert.Equal(1, exit);
        Assert.True(await IsAdminAsync(admin));
        Assert.Contains("last", error);
    }

    [Fact]
    public async Task Revoking_someone_who_is_not_an_admin_changes_nothing()
    {
        var (_, adminEmail) = await UserAsync();
        await RunAsync("grant", adminEmail);
        var (_, email) = await UserAsync();

        var (exit, output, _) = await RunAsync("revoke", email);

        Assert.Equal(0, exit);
        Assert.Contains("not an admin", output);
        Assert.Equal(1, await AdminCountAsync());
    }

    [Fact]
    public async Task Revoking_an_unknown_email_is_refused()
    {
        var unknown = ApiFixture.NewEmail();

        var (exit, _, error) = await RunAsync("revoke", unknown);

        Assert.Equal(1, exit);
        Assert.Contains(unknown, error);
    }

    [Fact]
    public async Task List_shows_each_admin_and_who_granted_them()
    {
        var (_, firstEmail) = await UserAsync();
        await RunAsync("grant", firstEmail);
        var (_, secondEmail) = await UserAsync();
        await RunAsync("grant", secondEmail, "--granted-by", firstEmail);

        var (exit, output, _) = await RunAsync("list");

        Assert.Equal(0, exit);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains(lines, l => l.Contains(firstEmail) && l.Contains("(bootstrap)"));
        Assert.Contains(lines, l => l.Contains(secondEmail) && l.Contains(firstEmail));
    }

    [Fact]
    public async Task Unexpected_failure_exits_with_3_and_a_one_line_message()
    {
        // e.g. the database is unreachable: a documented exit code, not an unhandled exception.
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddDbContext<BookingDbContext>(o => o.UseMySQL(UnreachableDatabase))
            .BuildServiceProvider();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await AdminCli.RunAsync(["list"], services, output, error);

        Assert.Equal(AdminCli.Failed, exit);
        Assert.Equal(3, exit);
        Assert.Empty(output.ToString());
        Assert.StartsWith("Failed:", error.ToString());
        Assert.Single(error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Nothing listens on port 1, so connecting fails at once.</summary>
    public const string UnreachableDatabase =
        "Server=127.0.0.1;Port=1;Database=unreachable;User ID=root;Password=x;Connection Timeout=2";

    [Theory]
    [InlineData()]
    [InlineData("unknown")]
    [InlineData("grant")]
    [InlineData("revoke")]
    [InlineData("grant", "a@example.test", "--granted-by")]
    [InlineData("grant", "a@example.test", "--unknown", "b@example.test")]
    [InlineData("revoke", "a@example.test", "extra")]
    [InlineData("list", "extra")]
    public async Task Usage_errors_exit_with_2_and_print_usage(params string[] args)
    {
        var (exit, _, error) = await RunAsync(args);

        Assert.Equal(2, exit);
        Assert.Contains("Usage", error);
        Assert.Equal(0, await AdminCountAsync());
    }
}
