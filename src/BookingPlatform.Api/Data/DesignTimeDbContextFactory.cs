using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BookingPlatform.Api.Data;

/// <summary>
/// Lets `dotnet ef` build the context without starting the API.
/// Uses BOOKING_DB if set, otherwise the docker-compose.yml MySQL.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BookingDbContext>
{
    public BookingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("BOOKING_DB")
            ?? "Server=localhost;Port=3307;Database=booking;User ID=root;Password=localdev;";
        return new BookingDbContext(
            new DbContextOptionsBuilder<BookingDbContext>().UseMySQL(connectionString).Options);
    }
}
