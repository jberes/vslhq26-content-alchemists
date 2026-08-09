using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Endpoints;
using Castmill.Api.Middleware;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Api.Services.Export;
using Castmill.Api.Services.Images;
using Castmill.Api.Services.Knowledge;
using Castmill.Api.Services.Media;
using Castmill.Api.Services.Publish;
using Castmill.Api.Services.Scout;
using Castmill.Api.Services.Secrets;
using Castmill.Api.Services.Seo;
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

// Secret-custody guard: constructing the cipher validates Castmill:EncryptionKey
// (present, base64, exactly 32 bytes) — a bad key stops the process here.
var secretCipher = new SecretCipher(builder.Configuration);

builder.Services.Configure<JwtOptions>(jwtSection);
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddSingleton<ISecretCipher>(secretCipher);
builder.Services.AddScoped<IUserSecretsService, UserSecretsService>();
builder.Services.AddSingleton<IBlobSasService, BlobSasService>();
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.SectionName));
builder.Services.AddSingleton<IPromptLog, PromptLog>();
builder.Services.AddScoped<IFoundryClientFactory, FoundryClientFactory>();
// Text-provider seam (ADR-020): Foundry serves every pass-1 generator; an optional
// non-Foundry provider serves the second-pass Tech Edit so it crosses model families.
builder.Services.AddTextProviders(builder.Configuration);
builder.Services.Configure<KnowledgeBaseOptions>(
    builder.Configuration.GetSection(KnowledgeBaseOptions.SectionName));
builder.Services.AddScoped<IKnowledgeBaseClient, KnowledgeBaseClient>();
builder.Services.AddScoped<IAiOrchestrator, AiOrchestrator>();
builder.Services.AddScoped<IBrandContextService, BrandContextService>();
builder.Services.AddScoped<ITranscriptionService, TranscriptionService>();
// Image path (B9): provider seam (ADR-015) → slot-accurate crop + headline
// compositing (ADR-013) → typed image plan (ADR-012).
builder.Services.AddImageProviders(builder.Configuration);
builder.Services.AddSingleton<IImageComposer, ImageComposer>();
builder.Services.AddScoped<IImagePlanService, ImagePlanService>();
builder.Services.AddScoped<IImageReferenceResolver, ImageReferenceResolver>();
builder.Services.AddScoped<IImageRenderer, ImageRenderer>();
builder.Services.AddScoped<IBrandLookup, BrandLookup>();
builder.Services.AddScoped<IResearchContextSuggester, ResearchContextSuggester>();
builder.Services.AddScoped<IBriefSuggester, BriefSuggester>();
builder.Services.AddScoped<IWorkspaceLinks, WorkspaceLinks>();
builder.Services.AddScoped<Castmill.Api.Services.Seo.ISeoResearch, Castmill.Api.Services.Seo.SeoResearch>();
builder.Services.AddScoped<Castmill.Api.Services.Seo.ISeoReportService, Castmill.Api.Services.Seo.SeoReportService>();
builder.Services.AddHostedService<InterruptedRunSweeper>();
// Its own client: a short timeout and no resilience retries, because re-fetching a slow
// third-party site would only make the user wait longer for the same answer.
builder.Services.AddHttpClient("brandlookup", c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.MaxResponseContentBufferSize = 1024 * 1024;
});
builder.Services.AddSingleton<IPublicContentStore, PublicContentStore>();
builder.Services.AddSingleton<IClipJobDispatcher, ClipJobDispatcher>();
builder.Services.Configure<PublishOptions>(builder.Configuration.GetSection(PublishOptions.SectionName));
builder.Services.Configure<SeoOptions>(builder.Configuration.GetSection(SeoOptions.SectionName));
builder.Services.AddScoped<IPublishBrokerClient, PublishBrokerClient>();
builder.Services.AddScoped<ISeoProvider, DataForSeoProvider>();
builder.Services.AddSingleton<IExportService, ExportService>();
// Optional git publishing (ADR-021): the customer's own repo, their own fine-grained PAT.
builder.Services.AddScoped<IContentInventory, ContentInventory>();
builder.Services.AddScoped<IContentScout, ContentScout>();
builder.Services.AddScoped<IGitHubClient, GitHubClient>();
builder.Services.AddScoped<IGitHubPublisher, GitHubPublisher>();

// Outbound HTTP: standard resilience (retry + circuit breaker + timeout) on
// every dependency (B8) — transient upstream blips never surface as user errors.
builder.Services.AddHttpClient("speech", client => client.Timeout = TimeSpan.FromMinutes(5))
    .AddStandardResilienceHandler();
builder.Services.AddHttpClient("broker").AddStandardResilienceHandler();
builder.Services.AddHttpClient(GitHubClient.HttpClientName,
        client => client.Timeout = TimeSpan.FromMinutes(3))
    .AddStandardResilienceHandler();
// DataForSEO's live LLM-response endpoints document an execution window of up to
// 120 seconds. The standard handler's 10-second attempt timeout aborts valid AEO
// work, so this client gets a provider-sized attempt and total budget.
builder.Services.AddHttpClient("seo", client => client.Timeout = TimeSpan.FromMinutes(6))
    .AddStandardResilienceHandler(options =>
    {
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(130);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(5);
    });
// Image renders run for 1–3 minutes. The standard handler's default 10-second attempt
// timeout (30s total) aborted every take before the model could answer — and each retry
// of a slow-but-succeeding render is a paid model call, so retries stay at the default
// transient-only conditions with an attempt window sized for a real render.
builder.Services.AddHttpClient("imageprovider", client => client.Timeout = TimeSpan.FromMinutes(6))
    .AddStandardResilienceHandler(options =>
    {
        options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(4);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(8);
    });
builder.Services.AddHttpClient("foundry-images", client => client.Timeout = TimeSpan.FromMinutes(6))
    .AddStandardResilienceHandler(options =>
    {
        options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(4);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(8);
    });
builder.Services.AddHttpClient(KnowledgeBaseClient.HttpClientName,
        client => client.Timeout = TimeSpan.FromSeconds(60))
    .AddStandardResilienceHandler();

/// <summary>
/// Azure SQL Serverless auto-pauses when idle, and the FIRST connection after that has to
/// wake it — which routinely takes longer than the client's default 30-second connect
/// timeout. The symptom is a pre-login TLS reset or
/// "Connection Timeout Expired ... [Post-Login] complete=29602", which reads like a broken
/// database and is really just a cold one.
///
/// Raising the timeout is done HERE rather than in the connection string because that string
/// lives in gitignored dev config and in Key Vault in production: this way every environment
/// gets the wake-up allowance without anyone editing a secret.
/// </summary>
static string WithResumeAllowance(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return connectionString ?? string.Empty;
    }

    var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);

    // Only ever raises it — an explicit longer timeout is respected.
    if (builder.ConnectTimeout < 60)
    {
        builder.ConnectTimeout = 60;
    }

    return builder.ConnectionString;
}

builder.Services.AddDbContext<CastmillDbContext>(options =>
    options.UseSqlServer(WithResumeAllowance(builder.Configuration.GetConnectionString("Castmill")),
        // Transient-fault retries for Azure SQL (B8 reliability). The window is sized for a
        // serverless resume, not a network blip: 8 tries backing off to 30s covers a cold
        // start that a 5x10s budget gave up on.
        sql => sql.EnableRetryOnFailure(
            maxRetryCount: 8, TimeSpan.FromSeconds(30), errorNumbersToAdd: null)));

// Telemetry (G7): registered only when a connection string is configured —
// the v3 SDK refuses to start with an empty one.
if (!string.IsNullOrWhiteSpace(builder.Configuration["ApplicationInsights:ConnectionString"]))
{
    builder.Services.AddApplicationInsightsTelemetry();
}

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
var aiPerMinute = builder.Configuration.GetValue("RateLimits:AiPerMinute", 30);

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

    // AI generation: the expensive partition — honest 429s beat surprise bills.
    options.AddPolicy("ai", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = aiPerMinute,
                Window = TimeSpan.FromMinutes(1),
            }));

    // External search/analysis providers (SEO), per user.
    options.AddPolicy("searches", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("RateLimits:SearchesPerMinute", 60),
                Window = TimeSpan.FromMinutes(1),
            }));
});

// CORS. The client shells are separate origins: the WASM dev server runs on its own port,
// and Static Web Apps serves the published client from a different host than App Service.
// Origins are configuration only — never a wildcard — and the Production guard above
// refuses a localhost origin. ETag and the correlation ID must be *exposed* explicitly or
// the browser hides them from the client, which would silently break conditional writes
// (If-Match) and client-to-server log correlation.
builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicyName, policy => policy
        .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        .WithHeaders("Authorization", "Content-Type", "If-Match", "X-Correlation-ID")
        .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
        .WithExposedHeaders("ETag", "X-Correlation-ID"));
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
// Before authentication: a rejected pre-flight must not depend on a token.
app.UseCors(CorsPolicyName);
// Authentication MUST precede the rate limiter: the "writes" policy partitions
// by user id, which is empty (one shared anonymous bucket) before auth runs.
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    // Development-only demo account, off unless Dev:SeedDemoUser is set. The seeder
    // refuses to run outside Development — see the security fence on DemoUserSeeder.
    //
    // A database that is asleep, resuming or briefly unreachable must NOT stop the process
    // from starting. Seeding a convenience account is not a startup invariant, and crashing
    // here turned "Azure SQL is waking up" into a dead `dotnet run` with a 200-line stack
    // trace. The app comes up, /health answers, and the seeder is retried on the next start.
    try
    {
        await DemoUserSeeder.SeedAsync(app);
    }
    catch (Microsoft.Data.SqlClient.SqlException ex)
    {
        app.Logger.LogWarning(
            "Demo user not seeded — the database did not answer ({Message}). If this is Azure "
            + "SQL Serverless it is probably resuming from auto-pause; the API is up and the "
            + "first request that reaches the database will wake it.",
            ex.Message.Split('\n')[0]);
    }

    // Dev-only: hands the seeded demo credentials to dev client builds so the sign-in
    // form can prefill. Same fence as the testbed: mapped ONLY in Development, and the
    // password itself lives only in the gitignored dev config.
    app.MapGet("/api/v1/dev/demo-credentials", (IConfiguration config) =>
            config.GetValue("Dev:SeedDemoUser", false)
                && config["Dev:DemoUserEmail"] is { Length: > 0 } email
                && config["Dev:DemoUserPassword"] is { Length: > 0 } password
                ? Results.Ok(new { email, password })
                : Results.NotFound())
        .AllowAnonymous()
        .RequireRateLimiting("auth");

    // Dev-only browser testbed (no Blazor needed): https://localhost:<port>/dev/testbed
    // Served from source, mapped only in Development, excluded from publish output.
    var testbed = Path.Combine(app.Environment.ContentRootPath, "DevTestbed", "index.html");
    app.MapGet("/dev/testbed", () => Results.File(testbed, "text/html")).AllowAnonymous();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

// Liveness above says only "the process is up". This one asks the DATABASE, which is the
// question that actually matters when Azure SQL Serverless is auto-paused: a green /health
// beside a client full of errors is exactly how a sleeping database gets misdiagnosed as a
// broken app.
app.MapGet("/health/db", async (CastmillDbContext db, CancellationToken ct) =>
{
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var ok = await db.Database.CanConnectAsync(ct);
        return Results.Ok(new
        {
            status = ok ? "healthy" : "unreachable",
            elapsedMs = stopwatch.ElapsedMilliseconds,
        });
    }
    catch (Microsoft.Data.SqlClient.SqlException ex)
    {
        // 503, not 500: the database being asleep is a temporary condition with a retry, not
        // a fault in this service.
        return Results.Problem(
            $"The database did not answer after {stopwatch.ElapsedMilliseconds} ms. "
            + "If this is Azure SQL Serverless it is resuming from auto-pause — retry shortly. "
            + ex.Message.Split('\n')[0],
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous();

app.MapAuthEndpoints();
app.MapCampaignEndpoints();
app.MapArtifactEndpoints();
app.MapAssetEndpoints();
app.MapBrandEndpoints();
app.MapSettingsEndpoints();
app.MapSecretsEndpoints();
app.MapBlobEndpoints();
app.MapAiEndpoints();
app.MapImageEndpoints();
app.MapImageSlotEndpoints();
app.MapMediaEndpoints();
app.MapPublishEndpoints();
app.MapExportEndpoints();
app.MapGitPublishEndpoints();
app.MapScheduleEndpoints();
app.MapSeoEndpoints();

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
public partial class Program
{
    /// <summary>Name of the single CORS policy; origins come from Cors:AllowedOrigins.</summary>
    internal const string CorsPolicyName = "castmill";
}
