using BookingPlatform.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BookingPlatform.IntegrationTests;

// T1: integration tests can create, migrate and drop a database.
public class TestDatabaseTests
{
    [Fact]
    public async Task Database_is_created_with_all_migrations_applied_and_dropped_on_dispose()
    {
        var database = new TestDatabase();
        await database.InitializeAsync();

        Assert.True(await TestDatabase.ExistsAsync(database.Name));
        await using (var context = database.CreateContext())
        {
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync());
        }

        await database.DisposeAsync();

        Assert.False(await TestDatabase.ExistsAsync(database.Name));
    }
}
