using System.Security.Claims;
using Castmill.Api.Data;
using Castmill.Core;
using Castmill.Core.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        // Anonymous endpoints carry the strict per-IP "auth" rate limit (brute-force control).
        var group = routes.MapGroup("/api/v1/auth").RequireRateLimiting("auth");

        group.MapPost("/register", RegisterAsync).AllowAnonymous();
        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/refresh", RefreshAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization("TenantAllowed");
        group.MapPost("/change-password", ChangePasswordAsync).RequireAuthorization("TenantAllowed");
        return routes;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        UserManager<CastmillUser> users,
        CastmillDbContext db,
        ITokenService tokens,
        TimeProvider clock,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // One tenant per user, created at registration — permanent binding (ADR-011).
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = request.DisplayName, CreatedAt = now };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);

        var user = new CastmillUser
        {
            Id = Guid.NewGuid(),
            UserName = request.Email,
            Email = request.Email,
            TenantId = tenant.Id,
            DisplayName = request.DisplayName,
            CreatedAt = now,
        };

        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            db.Tenants.Remove(tenant);
            await db.SaveChangesAsync(ct);
            // Identity's own error descriptions are safe to surface (password policy, duplicate email).
            return Results.ValidationProblem(result.Errors
                .GroupBy(e => e.Code, e => e.Description)
                .ToDictionary(g => g.Key, g => g.ToArray()));
        }

        await AuditAsync(db, tenant.Id, user.Id, "auth.register", now, ct);
        return Results.Ok(await IssueTokensAsync(user, familyId: Guid.NewGuid(), db, tokens, now, ct));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserManager<CastmillUser> users,
        SignInManager<CastmillUser> signIn,
        CastmillDbContext db,
        ITokenService tokens,
        TimeProvider clock,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var user = await users.FindByEmailAsync(request.Email);
        if (user is null)
        {
            // Identical response for unknown user and wrong password — no account enumeration.
            return Results.Unauthorized();
        }

        // lockoutOnFailure: repeated failures lock the account (brute-force defense in depth
        // on top of the per-IP rate limit).
        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            await AuditAsync(db, user.TenantId, user.Id,
                result.IsLockedOut ? "auth.login.lockout" : "auth.login.failed", now, ct);
            return Results.Unauthorized();
        }

        await AuditAsync(db, user.TenantId, user.Id, "auth.login", now, ct);
        return Results.Ok(await IssueTokensAsync(user, familyId: Guid.NewGuid(), db, tokens, now, ct));
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        UserManager<CastmillUser> users,
        CastmillDbContext db,
        ITokenService tokens,
        TimeProvider clock,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hash = tokens.HashRefreshToken(request.RefreshToken);
        var stored = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null)
        {
            return Results.Unauthorized();
        }

        if (!stored.IsActive(now))
        {
            // Reuse of a rotated/revoked token means the token may be stolen:
            // revoke the entire family so neither party can continue the session.
            await db.RefreshTokens
                .Where(t => t.FamilyId == stored.FamilyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
            var owner = await users.FindByIdAsync(stored.UserId.ToString());
            if (owner is not null)
            {
                await AuditAsync(db, owner.TenantId, owner.Id, "auth.refresh.reuse-detected", now, ct);
            }
            return Results.Unauthorized();
        }

        var user = await users.FindByIdAsync(stored.UserId.ToString());
        if (user is null)
        {
            return Results.Unauthorized();
        }

        stored.UsedAt = now; // rotation: each refresh token is single-use
        var response = await IssueTokensAsync(user, stored.FamilyId, db, tokens, now, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> LogoutAsync(
        ClaimsPrincipal principal,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var userId = GetUserId(principal);
        // Revoke every active refresh token for the user; the short-lived access
        // token simply expires (≤15 min) — nothing longer-lived survives logout.
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        await AuditAsync(db, GetTenantId(principal), userId, "auth.logout", now, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        ClaimsPrincipal principal,
        UserManager<CastmillUser> users,
        CastmillDbContext db,
        ITokenService tokens,
        TimeProvider clock,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var user = await users.FindByIdAsync(GetUserId(principal).ToString());
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            return Results.ValidationProblem(result.Errors
                .GroupBy(e => e.Code, e => e.Description)
                .ToDictionary(g => g.Key, g => g.ToArray()));
        }

        // Credential change invalidates every outstanding session.
        await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        await AuditAsync(db, user.TenantId, user.Id, "auth.password-changed", now, ct);

        return Results.Ok(await IssueTokensAsync(user, familyId: Guid.NewGuid(), db, tokens, now, ct));
    }

    private static async Task<AuthResponse> IssueTokensAsync(
        CastmillUser user,
        Guid familyId,
        CastmillDbContext db,
        ITokenService tokens,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var (access, accessExpires) = tokens.CreateAccessToken(user);
        var (plainRefresh, entity) = tokens.CreateRefreshToken(user.Id, familyId, now);
        db.RefreshTokens.Add(entity);
        await db.SaveChangesAsync(ct);
        return new AuthResponse(access, accessExpires, plainRefresh, entity.ExpiresAt);
    }

    private static async Task AuditAsync(
        CastmillDbContext db, Guid tenantId, Guid? userId, string action, DateTimeOffset now, CancellationToken ct)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Action = action,
            OccurredAt = now,
        });
        await db.SaveChangesAsync(ct);
    }

    internal static Guid GetUserId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? throw new InvalidOperationException("Authenticated principal without sub claim."));

    internal static Guid GetTenantId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue("tenant")
            ?? throw new InvalidOperationException("Authenticated principal without tenant claim."));
}
