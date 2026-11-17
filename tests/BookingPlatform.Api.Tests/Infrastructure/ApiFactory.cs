using System.Collections.Concurrent;
using System.Net.Http.Headers;
using BookingPlatform.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookingPlatform.Api.Tests.Infrastructure;

/// <summary>
/// Hosts the real API pipeline in memory, plus the probe endpoints in
/// <see cref="ProbeController"/>, which exist only in the test assembly.
/// No endpoint used here touches the database.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string SigningKey = "unit-test-signing-key-that-is-at-least-32-bytes-long";

    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Jwt:SigningKey", SigningKey);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureServices(services =>
            services.AddControllers().AddApplicationPart(typeof(ProbeController).Assembly));
    }

    public JwtOptions JwtOptions => Services.GetRequiredService<IOptions<JwtOptions>>().Value;

    /// <summary>A valid access token for <paramref name="userId"/>, issued by the app itself.</summary>
    public string TokenFor(Guid userId) => Services.GetRequiredService<AccessTokenIssuer>().Issue(userId).Token;

    public HttpClient ClientFor(Guid userId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(userId));
        return client;
    }
}

/// <summary>Records every log entry with the scopes active when it was written.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose() { }

    public sealed record LogEntry(
        string Category, LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> Scope);

    private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var scope = new Dictionary<string, object?>();
            provider._scopes.ForEachScope((s, dict) =>
            {
                if (s is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var (key, value) in pairs) dict[key] = value;
                }
            }, scope);
            provider.Entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception, scope));
        }
    }
}
