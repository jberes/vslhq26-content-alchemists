using Castmill.Api.Data;
using Castmill.Core;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Auth;

/// <summary>
/// Creates one demo account so the client shells have something to sign in with during
/// development, without anyone having to register by hand first.
///
/// SECURITY FENCE — this is a credential, so it is fenced three ways:
///   1. It only ever runs in the Development environment. <see cref="SeedAsync"/> throws if
///      called outside it, rather than quietly doing nothing, so a future refactor that
///      moves the call site cannot silently ship a known account to production.
///   2. It is off unless <c>Dev:SeedDemoUser</c> is true.
///   3. The password is NOT in the repository. It comes from <c>Dev:DemoUserPassword</c> in
///      the gitignored appsettings.Development.json; the committed template documents the
///      key with an empty value, and seeding is skipped (with a warning) if it is unset.
///
/// The account is created through the same UserManager + tenant path as /auth/register, so
/// it is an ordinary user with an ordinary tenant — no special casing anywhere else.
/// </summary>
public static class DemoUserSeeder
{
    public static async Task SeedAsync(WebApplication app, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!app.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "DemoUserSeeder is a development-only facility and must never run outside Development.");
        }

        var config = app.Configuration;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DemoUserSeeder));

        if (!config.GetValue("Dev:SeedDemoUser", false))
        {
            return;
        }

        var email = config["Dev:DemoUserEmail"];
        var password = config["Dev:DemoUserPassword"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning(
                "Dev:SeedDemoUser is on but Dev:DemoUserEmail/Dev:DemoUserPassword are not set in "
                + "appsettings.Development.json — no demo account was created.");
            return;
        }

        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<CastmillUser>>();
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        if (await users.FindByEmailAsync(email) is not null)
        {
            // IsEnabled guards here and below keep the arguments from being evaluated and
            // boxed when the level is off (CA1873).
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Demo account {Email} already exists.", email);
            }

            return;
        }

        var now = clock.GetUtcNow();
        var displayName = config["Dev:DemoUserDisplayName"] ?? "Demo user";

        // Same shape as registration: one tenant per user, permanently bound (ADR-011).
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = displayName, CreatedAt = now };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);

        var user = new CastmillUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            TenantId = tenant.Id,
            DisplayName = displayName,
            CreatedAt = now,
        };

        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            db.Tenants.Remove(tenant);
            await db.SaveChangesAsync(ct);

            // Most likely the configured password fails Identity's policy. Say so plainly;
            // the descriptions never contain the password itself.
            logger.LogWarning(
                "Demo account was not created: {Errors}",
                string.Join("; ", result.Errors.Select(e => e.Description)));
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Seeded demo account {Email} with tenant {TenantId}.", email, tenant.Id);
        }
    }
}
