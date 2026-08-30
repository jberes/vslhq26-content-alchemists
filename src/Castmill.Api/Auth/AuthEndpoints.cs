using System.Data;
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
        IAccountService accounts,
        CastmillDbContext db,
        IAuthTokenIssuer tokenIssuer,
        TimeProvider clock,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var account = await accounts.CreateAsync(
            request.Email, request.DisplayName, request.Password, ct: ct);
        if (!account.Succeeded)
        {
            // Identity's own error descriptions are safe to surface (password policy, duplicate email).
            return Results.ValidationProblem(account.Result.Errors
                .GroupBy(e => e.Code, e => e.Description)
                .ToDictionary(g => g.Key, g => g.ToArray()));
        }

        var user = account.User!;
        await AuditAsync(db, user.TenantId, user.Id, "auth.register", now, ct);
        return Results.Ok(await tokenIssuer.IssueAsync(user, Guid.NewGuid(), now, ct));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserManager<CastmillUser> users,
        SignInManager<CastmillUser> signIn,
        CastmillDbContext db,
        IAuthTokenIssuer tokenIssuer,
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
    return Results.Ok(await tokenIssuer.IssueAsync(user, Guid.NewGuid(), now, ct));
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        UserManager<CastmillUser> users,
        CastmillDbContext db,
        ITokenService tokens,
        IAuthTokenIssuer tokenIssuer,
        Microsoft.Extensions.Options.IOptions<JwtOptions> jwt,
        TimeProvider clock,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hash = tokens.HashRefreshToken(request.RefreshToken);
        var userId = await db.RefreshTokens.AsNoTracking()
            .Where(token => token.TokenHash == hash)
            .Select(token => (Guid?)token.UserId)
            .SingleOrDefaultAsync(ct);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var response = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                ct);
            await AcquireRefreshTokenLockAsync(db, userId.Value, ct);

            var stored = await db.RefreshTokens
                .SingleOrDefaultAsync(token => token.TokenHash == hash, ct);
            if (stored is null)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }

            var grace = TimeSpan.FromSeconds(Math.Max(0, jwt.Value.RefreshReuseGraceSeconds));
            var withinGrace = !stored.IsActive(now) && stored.IsWithinReuseGrace(now, grace);

            if (!stored.IsActive(now) && !withinGrace)
            {
                // Reuse of a rotated/revoked token means the token may be stolen:
                // revoke the entire family so neither party can continue the session.
                await db.RefreshTokens
                    .Where(token => token.FamilyId == stored.FamilyId && token.RevokedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), ct);
                var owner = await users.FindByIdAsync(stored.UserId.ToString());
                if (owner is not null)
                {
                    await AuditAsync(
                        db,
                        owner.TenantId,
                        owner.Id,
                        "auth.refresh.reuse-detected",
                        now,
                        ct);
                }
                await transaction.CommitAsync(ct);
                return null;
            }

            var user = await users.FindByIdAsync(stored.UserId.ToString());
            if (user is null)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }

            // Within the grace, a replay of a just-consumed token rotates AGAIN rather than
            // revoking the family. That converts the crash-mid-rotation, the two-window race and
            // the retried request from "your session has expired" into a non-event, at the cost
            // of a thief who replays within the window ALSO getting a token — after which the
            // next collision falls outside the grace and trips reuse detection as before. The
            // audit row keeps the event observable either way.
            if (withinGrace)
            {
                await AuditAsync(
                    db,
                    user.TenantId,
                    user.Id,
                    "auth.refresh.reused-within-grace",
                    now,
                    ct);
            }

            stored.UsedAt ??= now;
            var issued = await tokenIssuer.IssueAsync(user, stored.FamilyId, now, ct);
            await transaction.CommitAsync(ct);
            return issued;
        });

        return response is null ? Results.Unauthorized() : Results.Ok(response);
    }

    private static async Task<IResult> LogoutAsync(
        ClaimsPrincipal principal,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var userId = GetUserId(principal);
        var tenantId = GetTenantId(principal);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                ct);
            await AcquireRefreshTokenLockAsync(db, userId, ct);
            // Revoke every active refresh token for the user; the short-lived access
            // token simply expires (≤15 min) — nothing longer-lived survives logout.
            await db.RefreshTokens
                .Where(token => token.UserId == userId && token.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), ct);
            await AuditAsync(db, tenantId, userId, "auth.logout", now, ct);
            await transaction.CommitAsync(ct);
        });
        return Results.NoContent();
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        ClaimsPrincipal principal,
        UserManager<CastmillUser> users,
        CastmillDbContext db,
        IAuthTokenIssuer tokenIssuer,
        TimeProvider clock,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var user = await users.FindByIdAsync(GetUserId(principal).ToString());
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (!await users.HasPasswordAsync(user))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [ExternalAuthErrors.PasswordNotConfigured] =
                    ["This account does not have a local password."],
            });
        }

        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            return Results.ValidationProblem(result.Errors
                .GroupBy(e => e.Code, e => e.Description)
                .ToDictionary(g => g.Key, g => g.ToArray()));
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var response = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                ct);
            await AcquireRefreshTokenLockAsync(db, user.Id, ct);
            // Credential change invalidates every outstanding session before issuing the
            // replacement session returned to the user who changed the password.
            await db.RefreshTokens
                .Where(token => token.UserId == user.Id && token.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), ct);
            await AuditAsync(db, user.TenantId, user.Id, "auth.password-changed", now, ct);
            var issued = await tokenIssuer.IssueAsync(user, Guid.NewGuid(), now, ct);
            await transaction.CommitAsync(ct);
            return issued;
        });

        return Results.Ok(response);
    }

    internal static Task AcquireRefreshTokenLockAsync(
        CastmillDbContext db,
        Guid userId,
        CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = {"castmill:refresh-token:" + userId.ToString("N")},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 30000;
            IF @result < 0
                THROW 51000, 'Could not acquire the refresh-token session lock.', 1;
            """, ct);

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
