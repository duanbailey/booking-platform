using BookingPlatform.Api.Admin;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Authorization;
using BookingPlatform.Api.Bookings;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Events;
using BookingPlatform.Api.Observability;
using BookingPlatform.Api.Outbox;
using BookingPlatform.Api.Slots;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

// `admin ...` runs the operator script (AdminCli) and `db ...` the deployment's database setup (DbMigrateCli,
// T27a) instead of the web server. Their arguments are not passed to the host, so they can't be read as configuration.
var adminArgs = args is ["admin", .. var rest] ? rest : null;
var dbArgs = args is ["db", .. var dbRest] ? dbRest : null;
var cliMode = adminArgs is not null || dbArgs is not null;

var builder = WebApplication.CreateBuilder(cliMode ? [] : args);

// Structured logging: JSON lines on stdout, collected by CloudWatch (specs/07-nfr.md).
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true; // carries CorrelationId
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});
if (cliMode)
{
    // The script's own output owns stdout; only warnings and errors are logged, to stderr.
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Logging:LogLevel:Default"] = "Warning",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
    });
    builder.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(
        o => o.LogToStandardErrorThreshold = LogLevel.Trace);
}

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        ErrorResponses.Configure(o.JsonSerializerOptions);
        // Don't echo serializer internals back to clients.
        o.AllowInputFormatterExceptionMessages = false;
    })
    .ConfigureApiBehaviorOptions(o =>
    {
        o.InvalidModelStateResponseFactory = ErrorResponses.ValidationFailed;
        // No ProblemDetails bodies: error results (e.g. 415) stay empty, so the status code
        // pages below give them the shared Error shape.
        o.SuppressMapClientErrors = true;
    });
builder.Services.AddOpenApi();

// ConnectionStrings:BookingDb, with Database:User and Database:Password overriding its credentials (T27a).
builder.Services.AddDbContext<BookingDbContext>((services, options) =>
    options.UseMySQL(DatabaseConnection.FromConfiguration(services.GetRequiredService<IConfiguration>())));

builder.Services.AddSingleton(TimeProvider.System);

// Authentication: HS256 access tokens carrying only the user's ID (specs/07-nfr.md, ADR 0010).
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.Section))
    .Validate(o => System.Text.Encoding.UTF8.GetByteCount(o.SigningKey) >= JwtOptions.MinSigningKeyBytes,
        $"Jwt:SigningKey must be at least {JwtOptions.MinSigningKeyBytes} bytes.")
    .ValidateOnStart();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
    {
        bearer.MapInboundClaims = false; // keep "sub" as "sub"
        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Value.Issuer,
            ValidAudience = jwt.Value.Audience,
            IssuerSigningKey = AccessTokenIssuer.SigningKey(jwt.Value),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.Sub,
        };
    });
// Every endpoint needs a valid token unless it is marked [AllowAnonymous] (spec: global bearerAuth).
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddSingleton<AccessTokenIssuer>();
builder.Services.AddScoped<RefreshTokenService>();
builder.Services.AddScoped<AuthTokenService>();
builder.Services.AddScoped<InviteService>();
builder.Services.AddScoped<PasswordRecoveryService>();
builder.Services.AddScoped<StaffAccessGuard>();
builder.Services.AddSingleton<BusinessZones>();
builder.Services.AddScoped<BookingScheduler>();
builder.Services.AddScoped<BookingCanceller>();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<Passwords>();
builder.Services.AddScoped<AccessResolver>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<OutboxWriter>(); // specs/06-events.md: events go through the outbox
if (!cliMode) builder.Services.AddOutboxRelay(builder.Configuration, builder.Environment); // ADR 0008; not for the CLI commands
builder.Services.AddAuthRateLimiting(builder.Configuration);

var app = builder.Build();

if (adminArgs is not null)
{
    return await AdminCli.RunAsync(adminArgs, app.Services, Console.Out, Console.Error);
}
if (dbArgs is not null)
{
    return await DbMigrateCli.RunAsync(dbArgs, app.Configuration,
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DbMigrateCli).FullName!), Console.Out, Console.Error);
}

// Client IP and scheme from the proxies in front (T27a): loopback is always trusted, plus the networks in
// ForwardedHeaders:KnownNetworks (behind CloudFront and the internal load balancer, the VPC), across up to
// ForwardedHeaders:ForwardLimit hops (2 there). An untrusted hop ends the chain, so clients can't spoof their IP.
app.UseForwardedHeaders(TrustedProxies.Options(app.Configuration));
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ErrorHandlingMiddleware>();
// Any error status with an empty body (authentication challenges, unknown routes, wrong
// methods, unsupported media types, ...) gets the shared Error shape. Headers such as
// WWW-Authenticate and Allow are kept.
app.UseStatusCodePages(context =>
    ErrorResponses.WriteAsync(context.HttpContext, ApiException.ForStatus(context.HttpContext.Response.StatusCode)));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

// Off behind CloudFront (Https:Redirect = false), which enforces HTTPS itself: it reaches the load balancer over
// HTTP inside AWS, so the API sees X-Forwarded-Proto: http and would redirect forever (T27a).
if (app.Configuration.GetValue("Https:Redirect", true)) app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
// The load balancer's health check (T27a): shallow, so a database outage doesn't make it replace every task.
app.MapGet("/health", () => Results.Json(new { status = "ok" })).AllowAnonymous();

app.Run();
return 0;

// Exposed for WebApplicationFactory in tests.
public partial class Program;
