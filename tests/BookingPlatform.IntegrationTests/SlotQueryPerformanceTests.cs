using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using MySql.Data.MySqlClient;
using Xunit.Abstractions;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T28: a slot query for 30 days meets p95 under 300 ms (specs/07-nfr.md; ADR 0005 makes 30 days the worst case).
/// Opt-in (decided 2026-11-25): skipped unless BOOKING_PERF_TEST=1, so ordinary runs stay quick; CI runs it in its
/// own job. Run with: BOOKING_PERF_TEST=1 dotnet test --filter Category=Performance --logger "console;verbosity=detailed"
/// </summary>
public sealed class PerformanceFactAttribute : FactAttribute
{
    public const string Variable = "BOOKING_PERF_TEST";

    public PerformanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"Performance test: set {Variable}=1 to run it.";
    }
}

[Trait("Category", "Performance")]
public sealed class SlotQueryPerformanceTests(ApiFixture fixture, ITestOutputHelper output) : IClassFixture<ApiFixture>
{
    // The measurement (decided 2026-11-25): the full HTTP request through the in-process host against real MySQL,
    // all staff (no staffId filter), 20 untimed warm-up requests, then 200 timed sequential requests whose 30-day
    // windows start on different days so nothing is served from a cache; p95 is the 190th fastest.
    private const int WarmUp = 20;
    private const int Timed = 200;
    private const double TargetMilliseconds = 300;

    [PerformanceFact]
    public async Task A_30_day_slot_query_for_a_busy_business_has_p95_under_300_ms()
    {
        var seedClock = Stopwatch.StartNew();
        var data = await PerformanceData.SeedAsync(fixture.Database.ConnectionString);
        output.WriteLine($"Seeded in {seedClock.Elapsed.TotalSeconds:F1} s: {data.Summary}");

        var token = (await fixture.RegisterAsync()).GetProperty("accessToken").GetString()!;
        var client = fixture.ClientWithToken(token);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        async Task<(double Milliseconds, int Slots)> QueryAsync(int startOffset)
        {
            var from = today.AddDays(startOffset);
            var to = from.AddDays(29);
            var url = $"/v1/services/{data.ServiceId}/slots?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
            var clock = Stopwatch.StartNew();
            using var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsByteArrayAsync();
            clock.Stop();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(body);
            return (clock.Elapsed.TotalMilliseconds, json.RootElement.GetProperty("slots").GetArrayLength());
        }

        for (var i = 0; i < WarmUp; i++) await QueryAsync(i % 30);

        var timings = new List<double>(Timed);
        var slotCounts = new List<int>(Timed);
        for (var i = 0; i < Timed; i++)
        {
            var (milliseconds, slots) = await QueryAsync(i % 30);
            timings.Add(milliseconds);
            slotCounts.Add(slots);
        }

        timings.Sort();
        var p50 = timings[Timed / 2 - 1];
        var p95 = timings[(int)Math.Ceiling(Timed * 0.95) - 1];
        output.WriteLine(
            $"Slot query, 30 days, {PerformanceData.BusyStaff} staff: p50 {p50:F1} ms, p95 {p95:F1} ms, max {timings[^1]:F1} ms; " +
            $"slots per response {slotCounts.Min()}-{slotCounts.Max()}");

        // The data must make the query do real work, or a fast result proves nothing.
        Assert.True(slotCounts.Min() > 500, $"expected a busy calendar with many open slots, got {slotCounts.Min()}");
        Assert.True(p95 < TargetMilliseconds, $"p95 {p95:F1} ms is not under {TargetMilliseconds} ms");
    }
}

/// <summary>
/// Realistic data for T28 (decided 2026-11-25), written with multi-row INSERTs of generated values: a busy business
/// in America/New_York (20 staff on a 60-minute service with 10/5-minute buffers, availability 09:00-13:00 and
/// 14:00-18:00 Monday to Saturday, about 60% of possible bookings taken over the next 60 days, some cancelled ones,
/// time off and date exceptions, 30-minute step, 60-day advance window), among 500 other businesses with about 100k
/// bookings, so every query has to find its rows through an index. The random choices use a fixed seed (IDs and dates still vary per run).
/// </summary>
internal sealed record PerformanceData(Guid ServiceId, string Summary)
{
    public const int BusyStaff = PerformanceDataGenerator.BusyStaff;

    public static Task<PerformanceData> SeedAsync(string connectionString) => new PerformanceDataGenerator().SeedAsync(connectionString);
}

internal sealed class PerformanceDataGenerator
{
    public const int BusyStaff = 20;
    private const int OtherBusinesses = 500;
    private const int StaffPerOtherBusiness = 10;
    private const int BookingsPerOtherStaff = 20;
    private const int Customers = 2000;
    private const string Zone = "America/New_York";

    private readonly Random random = new(28);
    private readonly DateTime now = DateTime.UtcNow;
    private readonly List<string> users = [], businesses = [], services = [], staff = [], staffServices = [],
        rules = [], exceptions = [], timeOff = [], bookings = [];
    private readonly List<Guid> customers = [];

    public async Task<PerformanceData> SeedAsync(string connectionString)
    {
        var serviceId = Generate();
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await Insert(connection, "users (id, email, password_hash, name, time_zone, created_at)", users);
        await Insert(connection, "businesses (id, name, owner_user_id, time_zone, min_notice_minutes, cancellation_window_hours, max_advance_days, slot_step_minutes, created_at)", businesses);
        await Insert(connection, "services (id, business_id, name, duration_minutes, buffer_before_minutes, buffer_after_minutes, description, price, created_at)", services);
        await Insert(connection, "staff (id, user_id, business_id, removed_at, created_at)", staff);
        await Insert(connection, "staff_services (staff_id, service_id)", staffServices);
        await Insert(connection, "availability_rules (id, staff_id, day_of_week, start_local, end_local)", rules);
        await Insert(connection, "availability_exceptions (id, staff_id, date, start_local, end_local)", exceptions);
        await Insert(connection, "time_off (id, staff_id, start_utc, end_utc, created_at)", timeOff);
        await Insert(connection, "bookings (id, staff_id, service_id, customer_id, start_utc, end_utc, blocked_start_utc, blocked_end_utc, status, created_at, cancelled_at, cancelled_by, cancellation_reason, version)", bookings);
        await using (var analyze = new MySqlCommand("ANALYZE TABLE bookings, time_off, availability_rules, availability_exceptions, staff, staff_services", connection))
        {
            await analyze.ExecuteNonQueryAsync();
        }
        return new PerformanceData(serviceId,
            $"{users.Count} users, {businesses.Count} businesses, {staff.Count} staff, {rules.Count} weekly blocks, " +
            $"{exceptions.Count} date exceptions, {timeOff.Count} time off, {bookings.Count} bookings");
    }

    private Guid Generate()
    {
        for (var i = 0; i < Customers; i++) customers.Add(User());

        var busyService = GenerateBusyBusiness();
        for (var b = 0; b < OtherBusinesses; b++) GenerateOtherBusiness(b);
        return busyService;
    }

    private Guid GenerateBusyBusiness()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Zone);
        var business = Business("Busy Salon", maxAdvanceDays: 60);
        var service = Service(business, durationMinutes: 60, before: 10, after: 5);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
        TimeOnly[][] blocks = [[new(9, 0), new(13, 0)], [new(14, 0), new(18, 0)]];

        for (var s = 0; s < BusyStaff; s++)
        {
            var member = Staff(business, service);
            foreach (var day in new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" })
            {
                foreach (var block in blocks) rules.Add($"('{Guid.NewGuid()}','{member}','{day}','{block[0]:HH\\:mm\\:ss}','{block[1]:HH\\:mm\\:ss}')");
            }

            // Two date exceptions (a day off, a short day) and two afternoons of time off in the next 60 days.
            var dayOff = today.AddDays(random.Next(1, 60));
            var shortDay = today.AddDays(random.Next(1, 60));
            exceptions.Add($"('{Guid.NewGuid()}','{member}','{dayOff:yyyy-MM-dd}',NULL,NULL)");
            if (shortDay != dayOff) exceptions.Add($"('{Guid.NewGuid()}','{member}','{shortDay:yyyy-MM-dd}','10:00:00','12:00:00')");
            var offDays = new HashSet<DateOnly> { dayOff, shortDay };
            for (var t = 0; t < 2; t++)
            {
                var date = today.AddDays(random.Next(1, 60));
                offDays.Add(date);
                var start = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(14, 0)), zone);
                timeOff.Add($"('{Guid.NewGuid()}','{member}','{Utc(start)}','{Utc(start.AddHours(4))}','{Utc(now)}')");
            }

            // About 60% of the bookable starts on the 30-minute grid, buffers never overlapping (R1).
            for (var d = 0; d < 60; d++)
            {
                var date = today.AddDays(d);
                if (date.DayOfWeek == DayOfWeek.Sunday || offDays.Contains(date)) continue;
                foreach (var block in blocks)
                {
                    var blockedUntil = DateTime.MinValue;
                    for (var local = date.ToDateTime(block[0]); local.AddMinutes(60) <= date.ToDateTime(block[1]); local = local.AddMinutes(30))
                    {
                        var start = TimeZoneInfo.ConvertTimeToUtc(local, zone);
                        if (start.AddMinutes(-10) < blockedUntil || start < now || random.NextDouble() >= 0.6) continue;
                        Booking(member, service, start, 60, 10, 5, cancelled: random.NextDouble() < 0.1);
                        blockedUntil = start.AddMinutes(65);
                    }
                }
            }
        }
        return service;
    }

    private void GenerateOtherBusiness(int index)
    {
        var business = Business($"Business {index}", maxAdvanceDays: 21);
        var service = Service(business, durationMinutes: 45, before: 0, after: 15);
        for (var s = 0; s < StaffPerOtherBusiness; s++)
        {
            var member = Staff(business, service);
            foreach (var day in new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" })
            {
                rules.Add($"('{Guid.NewGuid()}','{member}','{day}','08:00:00','17:00:00')");
            }
            if (random.NextDouble() < 0.2)
            {
                var start = now.Date.AddDays(random.Next(-30, 60)).AddHours(13);
                timeOff.Add($"('{Guid.NewGuid()}','{member}','{Utc(start)}','{Utc(start.AddHours(5))}','{Utc(now)}')");
            }
            // Past and future bookings, one per hour slot so they never overlap.
            var used = new HashSet<int>();
            while (used.Count < BookingsPerOtherStaff)
            {
                var slot = random.Next(-30 * 9, 60 * 9);
                if (!used.Add(slot)) continue;
                var start = now.Date.AddDays(Math.Floor(slot / 9.0)).AddHours(13 + ((slot % 9) + 9) % 9);
                Booking(member, service, start, 45, 0, 15, cancelled: random.NextDouble() < 0.1);
            }
        }
    }

    private Guid User()
    {
        var id = Guid.NewGuid();
        users.Add($"('{id}','{id:N}@example.test','not-a-real-hash','Perf User','America/New_York','{Utc(now)}')");
        return id;
    }

    private Guid Business(string name, int maxAdvanceDays)
    {
        var id = Guid.NewGuid();
        businesses.Add($"('{id}','{name}','{User()}','{Zone}',120,24,{maxAdvanceDays},30,'{Utc(now)}')");
        return id;
    }

    private Guid Service(Guid business, int durationMinutes, int before, int after)
    {
        var id = Guid.NewGuid();
        services.Add($"('{id}','{business}','Service',{durationMinutes},{before},{after},NULL,45.00,'{Utc(now)}')");
        return id;
    }

    private Guid Staff(Guid business, Guid service)
    {
        var id = Guid.NewGuid();
        staff.Add($"('{id}','{User()}','{business}',NULL,'{Utc(now)}')");
        staffServices.Add($"('{id}','{service}')");
        return id;
    }

    private void Booking(Guid member, Guid service, DateTime start, int duration, int before, int after, bool cancelled)
    {
        var customer = customers[random.Next(customers.Count)];
        var end = start.AddMinutes(duration);
        var status = cancelled ? "Cancelled" : "Confirmed";
        var cancellation = cancelled ? $"'{Utc(now)}','{customer}','CustomerCancelled'" : "NULL,NULL,NULL";
        bookings.Add($"('{Guid.NewGuid()}','{member}','{service}','{customer}','{Utc(start)}','{Utc(end)}'," +
            $"'{Utc(start.AddMinutes(-before))}','{Utc(end.AddMinutes(after))}','{status}','{Utc(now)}',{cancellation},1)");
    }

    private static string Time(TimeOnly value) => value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Utc(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

    private static async Task Insert(MySqlConnection connection, string target, List<string> rows)
    {
        for (var i = 0; i < rows.Count; i += 1000)
        {
            var sql = new StringBuilder("INSERT INTO ").Append(target).Append(" VALUES ")
                .AppendJoin(',', rows.Skip(i).Take(1000)).ToString();
            await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync();
        }
    }
}
