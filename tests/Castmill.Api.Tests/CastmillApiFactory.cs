using System.Security.Cryptography;
using Castmill.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace Castmill.Api.Tests;

/// <summary>
/// Boots the real API against a throwaway SQL Server container (real migrations,
/// real Identity, real JWT pipeline). Secrets are generated per test run in
/// memory — nothing is read from or written to developer configuration.
/// </summary>
public sealed class CastmillApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _sql =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    // Per-run random key: tests prove the pipeline works without any shared secret.
    public string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    public async ValueTask InitializeAsync()
    {
        await _sql.StartAsync();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        await db.Database.MigrateAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _sql.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Castmill", _sql.GetConnectionString());
        builder.UseSetting("Jwt:SigningKey", SigningKey);
        // High enough that functional tests never trip it; the rate-limit test
        // uses its own factory with a tiny limit.
        builder.UseSetting("RateLimits:AuthPerMinute", "1000");
    }

    public IServiceScope CreateDbScope() => Services.CreateScope();
}
