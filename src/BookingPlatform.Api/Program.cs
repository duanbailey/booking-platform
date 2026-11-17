using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Authorization;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Observability;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Structured logging: JSON lines on stdout, collected by CloudWatch (specs/07-nfr.md).
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true; // carries CorrelationId
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

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

builder.Services.AddDbContext<BookingDbContext>((services, options) =>
    options.UseMySQL(services.GetRequiredService<IConfiguration>().GetConnectionString("BookingDb")
        ?? throw new InvalidOperationException("Connection string 'BookingDb' is not configured.")));

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
builder.Services.AddScoped<AccessResolver>();

var app = builder.Build();

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

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Exposed for WebApplicationFactory in tests.
public partial class Program;
