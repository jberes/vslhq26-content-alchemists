using System.Security.Claims;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

/// <summary>
/// Plaintext per-user settings (UI prefs, defaults). Secret kinds — anything
/// under the reserved "secret." prefix (Foundry credentials, broker tokens) —
/// are refused here; they require the AES-256-GCM store that lands in Phase B3.
/// </summary>
public static class SettingsEndpoints
{
    private const string SecretPrefix = "secret.";
    private const int MaxKeyLength = 100;

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/settings").RequireAuthorization("TenantAllowed");

        group.MapGet("/", ListAsync);
        group.MapPut("/{key}", UpsertAsync).Validate<SettingWriteRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{key}", DeleteAsync).RequireRateLimiting("writes");
        return routes;
    }

    private static IResult? GuardKey(string key)
    {
        if (key.Length is 0 or > MaxKeyLength || key.Any(char.IsWhiteSpace))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["key"] = [$"Keys must be 1-{MaxKeyLength} characters with no whitespace."],
            });
        }
        if (key.StartsWith(SecretPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Refusing is the security control: a secret stored through this
            // endpoint would sit in the database unencrypted.
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "Keys under 'secret.' require the encrypted secret store (Phase B3) and cannot be written here.");
        }
        return null;
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal, CastmillDbContext db, CancellationToken ct)
    {
        var userId = AuthEndpoints.GetUserId(principal);
        var settings = await db.UserSettings
            .Where(s => s.UserId == userId && !s.IsEncrypted)
            .OrderBy(s => s.Key)
            .Select(s => new SettingResponse(s.Key, s.Value, s.UpdatedAt))
            .ToListAsync(ct);
        return Results.Ok(settings);
    }

    private static async Task<IResult> UpsertAsync(
        string key,
        SettingWriteRequest request,
        ClaimsPrincipal principal,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (GuardKey(key) is { } invalid)
        {
            return invalid;
        }

        var userId = AuthEndpoints.GetUserId(principal);
        var setting = await db.UserSettings
            .SingleOrDefaultAsync(s => s.UserId == userId && s.Key == key, ct);

        if (setting is null)
        {
            setting = new UserSetting
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId!.Value,
                UserId = userId,
                Key = key,
                Value = request.Value,
                IsEncrypted = false,
                UpdatedAt = clock.GetUtcNow(),
            };
            db.UserSettings.Add(setting);
        }
        else
        {
            if (setting.IsEncrypted)
            {
                return Results.Conflict("This key holds an encrypted value and cannot be overwritten here.");
            }
            setting.Value = request.Value;
            setting.UpdatedAt = clock.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new SettingResponse(setting.Key, setting.Value, setting.UpdatedAt));
    }

    private static async Task<IResult> DeleteAsync(
        string key, ClaimsPrincipal principal, CastmillDbContext db, CancellationToken ct)
    {
        var userId = AuthEndpoints.GetUserId(principal);
        var setting = await db.UserSettings
            .SingleOrDefaultAsync(s => s.UserId == userId && s.Key == key && !s.IsEncrypted, ct);
        if (setting is null)
        {
            return Results.NotFound();
        }

        db.UserSettings.Remove(setting);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
