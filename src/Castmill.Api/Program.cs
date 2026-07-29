using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Endpoints;
using Castmill.Api.Middleware;
using Castmill.Api.Tenancy;
using Castmill.Core.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Startup security guards — misconfiguration means the process does not start.
// Secrets arrive via user-secrets (dev) or environment/Key Vault (prod);
// appsettings*.json committed to the repo never contain key material.
// ---------------------------------------------------------------------------
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
var signingKey = jwtSection["SigningKey"];
if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
{
    throw new InvalidOperationException(
        "Jwt:SigningKey is missing or shorter than 256 bits. " +
        "Dev: dotnet user-secrets set Jwt:SigningKey \"$(openssl rand -base64 48)\" " +
        "(from src/Castmill.Api). Prod: App Service setting / Key Vault reference. " +
        "Never put this value in appsettings.json.");
}

if (builder.Environment.IsProduction())
{
    var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    if (corsOrigins.Any(o => o.Contains("localhost", StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException("Production refuses localhost CORS origins.");
    }
}

builder.Services.Configure<JwtOptions>(jwtSection);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
builder.Services.AddScoped<ITokenService, TokenService>();

builder.Services.AddDbContext<CastmillDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Castmill")));

builder.Services
    .AddIdentityCore<CastmillUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        // Length is the primary password control (NIST 800-63B — no composition
        // rules); Identity's hasher is PBKDF2 with per-user salt.
        options.Password.RequiredLength = 12;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireDigit = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireLowercase = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddSignInManager()
    .AddEntityFrameworkStores<CastmillDbContext>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"] ?? "castmill-api",
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"] ?? "castmill-clients",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            // Only the algorithm we sign with is accepted — no downgrade surface.
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Every business route requires an authenticated user with a valid tenant claim.
    options.AddPolicy("TenantAllowed", policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx =>
            Guid.TryParse(ctx.User.FindFirstValue(HttpContextTenantProvider.TenantClaim), out _)));
});

// Fixed-window limits (ADR-009); values are config-tunable, never disabled.
var authPerMinute = builder.Configuration.GetValue("RateLimits:AuthPerMinute", 10);
var writesPerMinute = builder.Configuration.GetValue("RateLimits:WritesPerMinute", 60);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Anonymous auth endpoints: strict fixed window per client IP.
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = authPerMinute,
                Window = TimeSpan.FromMinutes(1),
            }));

    // Authenticated writes: partitioned by user id (ADR-009).
    options.AddPolicy("writes", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = writesPerMinute,
                Window = TimeSpan.FromMinutes(1),
            }));
});

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddOpenApi();
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseMiddleware<CorrelationIdMiddleware>();
// Authentication MUST precede the rate limiter: the "writes" policy partitions
// by user id, which is empty (one shared anonymous bucket) before auth runs.
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    // Dev-only browser testbed (no Blazor needed): https://localhost:<port>/dev/testbed
    // Served from source, mapped only in Development, excluded from publish output.
    var testbed = Path.Combine(app.Environment.ContentRootPath, "DevTestbed", "index.html");
    app.MapGet("/dev/testbed", () => Results.File(testbed, "text/html")).AllowAnonymous();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.MapAuthEndpoints();
app.MapCampaignEndpoints();
app.MapArtifactEndpoints();
app.MapAssetEndpoints();
app.MapBrandEndpoints();
app.MapSettingsEndpoints();

app.MapGet("/api/v1/me", (ClaimsPrincipal principal) =>
    {
        var email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email") ?? string.Empty;
        var name = principal.FindFirstValue("name") ?? string.Empty;
        return Results.Ok(new MeResponse(
            AuthEndpoints.GetUserId(principal),
            AuthEndpoints.GetTenantId(principal),
            email,
            name));
    })
    .RequireAuthorization("TenantAllowed");

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
