using System.Text.Json;
using BookingPlatform.Api.Events;
using BookingPlatform.IntegrationTests.Infrastructure;
using BookingPlatform.Tests.Shared;
using Microsoft.AspNetCore.Http;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T23 review (2026-11-23): the API and the worker validate events with different libraries, so the payloads the API
/// actually publishes are kept as examples in specs/events/examples, exactly as the relay sends them: written by the
/// real OutboxWriter and read back from MySQL's JSON column (canonical form, T21 review). This test fails when an
/// event's payload changes without its example; the worker's tests validate every example (worker/src/examples.test.ts).
/// Regenerate after an intended change with UPDATE_EVENT_EXAMPLES=1.
/// </summary>
public class EventExamplesTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    /// <summary>Every example uses this eventId, since the writer makes a new one each time.</summary>
    private const string ExampleEventId = "0190a000-0000-7000-8000-0000000000ff";

    private static readonly DateTimeOffset ExampleTime = new(2026, 11, 30, 8, 0, 0, TimeSpan.Zero);

    private static string ExamplesDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "specs", "events");
            if (Directory.Exists(candidate)) return Path.Combine(candidate, "examples");
        }
        throw new InvalidOperationException("specs/events not found above the test binaries.");
    }

    /// <summary>The payload as the relay would send it: written by the OutboxWriter, read back from MySQL.</summary>
    private async Task<(string FileName, string Payload)> PublishedFormAsync(DomainEvent domainEvent)
    {
        await using var db = fixture.Database.CreateContext();
        var writer = new OutboxWriter(db, new MutableTimeProvider(ExampleTime), new HttpContextAccessor());
        var message = writer.Add(domainEvent, correlationId: "example");
        await db.SaveChangesAsync();
        var payload = Convert.ToString(await fixture.Sql.ScalarAsync("SELECT payload FROM outbox WHERE id = @id", new { id = message.Id }))!;
        payload = payload.Replace(message.Id.ToString(), ExampleEventId);
        return ($"{message.EventType}.v{message.SchemaVersion}.json", payload);
    }

    public static TheoryData<string> EventTypes() => [.. SampleEvents.All.Keys];

    [Theory]
    [MemberData(nameof(EventTypes))]
    public async Task Each_event_the_API_writes_matches_its_checked_in_example(string eventType)
    {
        var (fileName, payload) = await PublishedFormAsync(SampleEvents.All[eventType]);
        EventSchemas.AssertValid(payload);
        var path = Path.Combine(ExamplesDir(), fileName);

        if (Environment.GetEnvironmentVariable("UPDATE_EVENT_EXAMPLES") == "1")
        {
            Directory.CreateDirectory(ExamplesDir());
            await File.WriteAllTextAsync(path, payload + "\n");
            return;
        }

        Assert.True(File.Exists(path), $"Missing {path}; run the tests with UPDATE_EVENT_EXAMPLES=1 to create it.");
        Assert.True(payload == (await File.ReadAllTextAsync(path)).TrimEnd('\n'),
            $"{fileName} no longer matches what the API writes; if the change is intended, regenerate with UPDATE_EVENT_EXAMPLES=1 (and check the worker still accepts it).");
    }

    [Fact]
    public void There_is_no_example_for_an_event_the_API_no_longer_writes()
    {
        var expected = SampleEvents.All.Keys.Select(type => $"{type}.v").ToList();
        var stale = Directory.GetFiles(ExamplesDir(), "*.json").Select(Path.GetFileName)
            .Where(file => !expected.Any(prefix => file!.StartsWith(prefix, StringComparison.Ordinal))).ToList();
        Assert.Empty(stale);
    }

    [Fact]
    public void Examples_are_valid_json_with_the_example_event_id()
    {
        foreach (var file in Directory.GetFiles(ExamplesDir(), "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            Assert.Equal(ExampleEventId, doc.RootElement.GetProperty("eventId").GetString());
        }
    }
}
