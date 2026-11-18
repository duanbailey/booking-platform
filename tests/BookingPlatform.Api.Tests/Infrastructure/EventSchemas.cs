using System.Collections.Concurrent;
using System.Text.Json;
using Corvus.Json;
using CorvusSchema = Corvus.Json.Validator.JsonSchema;

namespace BookingPlatform.Tests.Shared;

/// <summary>
/// Validates event envelopes against the JSON Schemas in specs/events/ (draft 2020-12),
/// asserting <c>format</c> (uuid, date-time) as well. Shared by the unit and integration
/// test projects.
/// </summary>
public static class EventSchemas
{
    // Schemas reference each other by relative file name, so each file is registered under
    // a URI in one folder; nothing is fetched from disk or the network during validation.
    private const string BaseUri = "https://schemas.booking.test/events/";

    public static string Directory { get; } = FindDirectory();

    private static readonly CorvusSchema.Options Options = new(
        additionalDocumentResolver: Resolver(),
        allowFileSystemAndHttpResolution: false,
        fallbackVocabulary: null,
        alwaysAssertFormat: true);

    private static readonly ConcurrentDictionary<string, CorvusSchema> Cache = new();

    private static IDocumentResolver Resolver()
    {
        var resolver = new PrepopulatedDocumentResolver();
        foreach (var path in System.IO.Directory.GetFiles(Directory, "*.schema.json"))
        {
            resolver.AddDocument(BaseUri + Path.GetFileName(path), JsonDocument.Parse(File.ReadAllText(path)));
        }
        return resolver;
    }

    /// <summary>Event types with a schema file, e.g. "BookingCreated" (not envelope or common).</summary>
    public static IReadOnlyList<string> EventTypes { get; } = System.IO.Directory
        .GetFiles(Directory, "*.v1.schema.json")
        .Select(path => Path.GetFileName(path)[..^".v1.schema.json".Length])
        .Where(name => name is not "envelope" and not "common")
        .Order()
        .ToList();

    /// <summary>Validates against the schema named by the envelope's eventType and schemaVersion.</summary>
    public static IReadOnlyList<string> Errors(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var file = $"{root.GetProperty("eventType").GetString()}.v{root.GetProperty("schemaVersion").GetInt32()}.schema.json";
        return Errors(json, file);
    }

    public static IReadOnlyList<string> Errors(string json, string schemaFile)
    {
        var path = Path.Combine(Directory, schemaFile);
        if (!File.Exists(path)) return [$"no schema file {schemaFile}"];

        var schema = Cache.GetOrAdd(schemaFile, f => CorvusSchema.From(BaseUri + f, Options));
        using var document = JsonDocument.Parse(json);
        var result = schema.Validate(document.RootElement, ValidationLevel.Detailed);
        return result.IsValid
            ? []
            : result.Results.Where(r => !r.Valid).Select(r => $"{r.Location}: {r.Message}").DefaultIfEmpty("invalid").ToList();
    }

    public static void AssertValid(string json)
    {
        var errors = Errors(json);
        Assert.True(errors.Count == 0, $"{json}\n{string.Join("\n", errors)}");
    }

    private static string FindDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "specs", "events");
            if (System.IO.Directory.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("specs/events not found above " + AppContext.BaseDirectory);
    }
}
