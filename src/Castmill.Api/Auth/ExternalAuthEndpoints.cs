using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Castmill.Api.Data;
using Castmill.Core;
using Castmill.Core.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Auth;

public static class ExternalAuthEndpoints
{
    internal const string FinishedPath = "/api/v1/auth/external/finished";

    public static IEndpointRouteBuilder MapExternalAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/auth/external");
        group.MapGet("/providers", GetProviders).AllowAnonymous().RequireRateLimiting("auth");
        group.MapPost("/start", StartAsync).AllowAnonymous().RequireRateLimiting("external-start");
        group.MapMethods("/start", ["GET", "PUT", "PATCH", "DELETE"],
            () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed))
            .AllowAnonymous()
            .RequireRateLimiting("external-start");
        group.MapGet("/browser/{attemptId:guid}", BrowserAsync)
            .AllowAnonymous()
            .RequireRateLimiting("external-flow");
        group.MapGet("/finished", Finished).AllowAnonymous().RequireRateLimiting("external-flow");
        group.MapPost("/poll", PollAsync).AllowAnonymous().RequireRateLimiting("external-poll");
        group.MapPost("/exchange", ExchangeAsync)
            .AllowAnonymous()
            .RequireRateLimiting("external-flow");
        group.MapGet("/links", GetLinksAsync)
            .RequireAuthorization("TenantAllowed")
            .RequireRateLimiting("auth");
        group.MapPost("/link/start", LinkStartAsync)
            .RequireAuthorization("TenantAllowed")
            .RequireRateLimiting("external-start");
        group.MapPost("/link/exchange", LinkExchangeAsync)
            .RequireAuthorization("TenantAllowed")
            .RequireRateLimiting("external-flow");
        group.MapDelete("/link/{provider}", UnlinkAsync)
            .RequireAuthorization("TenantAllowed")
            .RequireRateLimiting("writes");
        return routes;
    }

    internal static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    internal static bool IsValidWebSignInReturnUri(string value, bool isProduction)
        => IsValidWebReturnUri(value, "/sign-in", isProduction);

    internal static bool IsValidWebAccountSettingsReturnUri(string value, bool isProduction)
        => IsValidWebReturnUri(value, "/settings/security", isProduction);

    private static bool IsValidWebReturnUri(string value, string expectedPath, bool isProduction)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            || !string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query))
        {
            return false;
        }

        if (isProduction)
        {
            return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                && !uri.IsLoopback;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || uri.IsLoopback;
    }

    internal static async Task<string> ReturnUriForAttemptAsync(
        Guid attemptId,
        CastmillDbContext db,
        ExternalAuthOptions options,
        CancellationToken ct,
        string? exchangeCode = null,
        string? errorCode = null)
    {
        var route = await db.ExternalAuthAttempts
            .AsNoTracking()
            .Where(attempt => attempt.Id == attemptId)
            .Select(attempt => new
            {
                attempt.ClientKind,
                attempt.ReturnRouteKey,
                attempt.LoopbackReturnUri,
            })
            .SingleOrDefaultAsync(ct);

        var baseUri = route is
        {
            ClientKind: ExternalAuthClientKinds.Web,
            ReturnRouteKey: ExternalAuthReturnRoutes.SignIn,
        }
            ? options.Clients.Web.SignInReturnUri
            : route is
            {
                ClientKind: ExternalAuthClientKinds.Web,
                ReturnRouteKey: ExternalAuthReturnRoutes.AccountSettings,
            }
                ? options.Clients.Web.AccountSettingsReturnUri
            : route is { ClientKind: ExternalAuthClientKinds.Desktop, LoopbackReturnUri: not null }
                ? route.LoopbackReturnUri
                : FinishedPath;
        if (string.Equals(baseUri, FinishedPath, StringComparison.Ordinal))
        {
            return baseUri;
        }

        var values = new Dictionary<string, string?>
        {
            ["external"] = "complete",
            ["attemptId"] = attemptId.ToString("D"),
        };
        if (exchangeCode is not null)
        {
            values["code"] = exchangeCode;
        }
        if (errorCode is not null)
        {
            values["error"] = errorCode;
        }

        if (route!.ClientKind == ExternalAuthClientKinds.Web)
        {
            var fragment = string.Join('&', values.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));
            return new UriBuilder(baseUri) { Fragment = fragment }.Uri.AbsoluteUri;
        }

        return QueryHelpers.AddQueryString(baseUri, values);
    }

    private static IResult GetProviders(IOptions<ExternalAuthOptions> options) => Results.Ok(
        new ExternalAuthProviderStatusResponse(
        [
            new(ExternalAuthProviders.Microsoft,
                ExternalAuthSchemes.IsConfigured(options.Value.Providers.Microsoft)),
            new(ExternalAuthProviders.Google,
                ExternalAuthSchemes.IsConfigured(options.Value.Providers.Google)),
        ]));

    private static async Task<IResult> GetLinksAsync(
        ClaimsPrincipal principal,
        UserManager<CastmillUser> users,
        IOptions<ExternalAuthOptions> options)
    {
        var user = await users.FindByIdAsync(AuthEndpoints.GetUserId(principal).ToString());
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var logins = await users.GetLoginsAsync(user);
        var linkedProviders = logins
            .Select(login => login.LoginProvider)
            .ToHashSet(StringComparer.Ordinal);
        return Results.Ok(new ExternalAuthLinksResponse(
            await users.HasPasswordAsync(user),
            [
                new(
                    ExternalAuthProviders.Microsoft,
                    ExternalAuthSchemes.IsConfigured(options.Value.Providers.Microsoft),
                    linkedProviders.Contains(ExternalAuthProviders.Microsoft)),
                new(
                    ExternalAuthProviders.Google,
                    ExternalAuthSchemes.IsConfigured(options.Value.Providers.Google),
                    linkedProviders.Contains(ExternalAuthProviders.Google)),
            ]));
    }

    private static async Task<IResult> StartAsync(
        ExternalAuthStartRequest request,
        HttpRequest httpRequest,
        CastmillDbContext db,
        IOptions<ExternalAuthOptions> options,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!string.Equals(
                request.ReturnRouteKey,
                ExternalAuthReturnRoutes.SignIn,
                StringComparison.Ordinal))
        {
            return Error(StatusCodes.Status400BadRequest, ExternalAuthErrors.InvalidRequest);
        }

        return await CreateAttemptAsync(request, null, httpRequest, db, options.Value, clock, ct);
    }

    private static async Task<IResult> LinkStartAsync(
        ExternalAuthStartRequest request,
        ClaimsPrincipal principal,
        HttpRequest httpRequest,
        CastmillDbContext db,
        IOptions<ExternalAuthOptions> options,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!string.Equals(
                request.ReturnRouteKey,
                ExternalAuthReturnRoutes.AccountSettings,
                StringComparison.Ordinal))
        {
            return Error(StatusCodes.Status400BadRequest, ExternalAuthErrors.InvalidRequest);
        }

        return await CreateAttemptAsync(
            request,
            AuthEndpoints.GetUserId(principal),
            httpRequest,
            db,
            options.Value,
                clock,
                ct);
            }

    private static async Task<IResult> CreateAttemptAsync(
        ExternalAuthStartRequest request,
        Guid? linkUserId,
        HttpRequest httpRequest,
        CastmillDbContext db,
        ExternalAuthOptions options,
        TimeProvider clock,
        CancellationToken ct)
    {
        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(
                request,
                new ValidationContext(request),
                validationResults,
                validateAllProperties: true))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [ExternalAuthErrors.InvalidRequest] = validationResults
                    .Select(result => result.ErrorMessage ?? "The request is invalid.")
                    .ToArray(),
            });
        }

        var credentials = CredentialsFor(request.Provider, options);
        if (credentials is null)
        {
            return Error(StatusCodes.Status400BadRequest, ExternalAuthErrors.InvalidProvider);
        }

        if (!ExternalAuthSchemes.IsConfigured(credentials))
        {
            return Error(StatusCodes.Status503ServiceUnavailable, ExternalAuthErrors.ProviderUnavailable);
        }

        var now = clock.GetUtcNow();
        string? loopbackReturnUri = null;
        if (request.ClientKind == ExternalAuthClientKinds.Desktop)
        {
            if (!TryValidateLoopbackReturnUri(request.LoopbackReturnUri, out loopbackReturnUri))
            {
                return Error(StatusCodes.Status400BadRequest, ExternalAuthErrors.InvalidRequest);
            }
        }
        else if (request.LoopbackReturnUri is not null)
        {
            return Error(StatusCodes.Status400BadRequest, ExternalAuthErrors.InvalidRequest);
        }

        var expiresAt = now.AddMinutes(options.AttemptLifetimeMinutes);
        var pollSecret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var attempt = new ExternalAuthAttempt
        {
            Id = Guid.NewGuid(),
            Provider = request.Provider,
            ClientKind = request.ClientKind,
            ReturnRouteKey = request.ReturnRouteKey,
            CodeChallenge = request.CodeChallenge,
            PollSecretHash = HashSecret(pollSecret),
            LoopbackReturnUri = loopbackReturnUri,
            Status = ExternalAuthStatuses.Pending,
            LinkUserId = linkUserId,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };
        db.ExternalAuthAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);

        var browserUrl = $"{httpRequest.PathBase}/api/v1/auth/external/browser/{attempt.Id:D}";
        return Results.Ok(new ExternalAuthStartResponse(
            attempt.Id,
            browserUrl,
            pollSecret,
            expiresAt));
    }

    internal static bool TryValidateLoopbackReturnUri(string? value, out string? normalized)
    {
        normalized = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || !string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
            || uri.Port < 1024
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3
            || segments[0] != "castmill"
            || segments[1] != "auth"
            || segments[2].Length != 43
            || segments[2].Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
            || !uri.AbsolutePath.EndsWith('/'))
        {
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    private static async Task<IResult> BrowserAsync(
        Guid attemptId,
        CastmillDbContext db,
        IOptions<ExternalAuthOptions> options,
        TimeProvider clock,
        CancellationToken ct)
    {
        var attempt = await db.ExternalAuthAttempts.SingleOrDefaultAsync(a => a.Id == attemptId, ct);
        if (attempt is null || attempt.Status != ExternalAuthStatuses.Pending)
        {
            return Error(StatusCodes.Status400BadRequest, ExternalAuthErrors.AttemptFailed);
        }

        if (attempt.ExpiresAt <= clock.GetUtcNow())
        {
            await MarkExpiredAsync(db, attempt.Id, ct);
            return Error(StatusCodes.Status410Gone, ExternalAuthErrors.AttemptExpired);
        }

        var credentials = CredentialsFor(attempt.Provider, options.Value);
        if (credentials is null || !ExternalAuthSchemes.IsConfigured(credentials))
        {
            await MarkFailedAsync(db, attempt.Id, ExternalAuthErrors.ProviderUnavailable, ct);
            return Error(StatusCodes.Status503ServiceUnavailable, ExternalAuthErrors.ProviderUnavailable);
        }

        var properties = new AuthenticationProperties
        {
            RedirectUri = FinishedPath,
        };
        properties.Items[ExternalAuthSchemes.AttemptIdProperty] = attempt.Id.ToString("D");
        properties.Items[ExternalAuthSchemes.ProviderProperty] = attempt.Provider;
        return Results.Challenge(properties, [ExternalAuthSchemes.SchemeFor(attempt.Provider)]);
    }

    private static IResult Finished(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
        response.Headers.XContentTypeOptions = "nosniff";
        return Results.Content(
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Castmill</title></head>"
            + "<body><main><h1>Authentication complete</h1><p>Return to Castmill.</p></main></body></html>",
            "text/html",
            Encoding.UTF8);
    }

    private static async Task<IResult> PollAsync(
        ExternalAuthPollRequest request,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var attempt = request.AttemptId == Guid.Empty
            ? null
            : await db.ExternalAuthAttempts.SingleOrDefaultAsync(a => a.Id == request.AttemptId, ct);
        if (attempt is null
            || string.IsNullOrWhiteSpace(request.PollSecret)
            || !SecretMatches(request.PollSecret, attempt.PollSecretHash))
        {
            return Results.Unauthorized();
        }

        if (attempt.ExpiresAt <= clock.GetUtcNow()
            && attempt.Status is ExternalAuthStatuses.Pending
                or ExternalAuthStatuses.Completing
                or ExternalAuthStatuses.Completed)
        {
            await MarkExpiredAsync(db, attempt.Id, ct);
            return Results.Ok(new ExternalAuthPollResponse(
                ExternalAuthStatuses.Expired,
                attempt.ExpiresAt,
                ExternalAuthErrors.AttemptExpired));
        }

        var responseStatus = attempt.Status == ExternalAuthStatuses.Completing
            ? ExternalAuthStatuses.Pending
            : attempt.Status;
        return Results.Ok(new ExternalAuthPollResponse(
            responseStatus,
            attempt.ExpiresAt,
            attempt.ErrorCode));
    }

    private static async Task<IResult> ExchangeAsync(
        ExternalAuthExchangeRequest request,
        CastmillDbContext db,
        UserManager<CastmillUser> users,
        IAccountService accounts,
        IAuthTokenIssuer tokenIssuer,
        TimeProvider clock,
        CancellationToken ct)
    {
        var attempt = request.AttemptId == Guid.Empty
            ? null
            : await db.ExternalAuthAttempts.AsNoTracking()
                .SingleOrDefaultAsync(a => a.Id == request.AttemptId, ct);
        if (attempt is null
            || string.IsNullOrWhiteSpace(request.ExchangeCode)
            || !SecretMatches(request.ExchangeCode, attempt.ExchangeCodeHash))
        {
            return Results.Unauthorized();
        }

        var now = clock.GetUtcNow();
        if (attempt.ExpiresAt <= now)
        {
            await MarkExpiredAsync(db, attempt.Id, ct);
            return Error(StatusCodes.Status410Gone, ExternalAuthErrors.AttemptExpired);
        }

        if (attempt.LinkUserId is not null)
        {
            return Error(StatusCodes.Status409Conflict, ExternalAuthErrors.ExchangeNotAllowed);
        }

        if (attempt.Status == ExternalAuthStatuses.Consumed || attempt.ConsumedAt is not null)
        {
            await AuditReplayAsync(db, users, attempt, now, ct);
            return Error(StatusCodes.Status409Conflict, ExternalAuthErrors.CodeConsumed);
        }

        if (attempt.Status == ExternalAuthStatuses.Pending)
        {
            return Error(StatusCodes.Status409Conflict, ExternalAuthErrors.AttemptPending);
        }

        if (attempt.Status != ExternalAuthStatuses.Completed
            || !HasCandidate(attempt))
        {
            return Error(
                StatusCodes.Status409Conflict,
                attempt.ErrorCode ?? ExternalAuthErrors.AttemptFailed);
        }

        if (!IsValidCodeVerifier(request.CodeVerifier)
            || !CodeVerifierMatches(request.CodeVerifier, attempt.CodeChallenge))
        {
            return Results.Unauthorized();
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var outcome = await ExecuteOutcomeAsync(
            operation => strategy.ExecuteAsync(operation),
            async () =>
            {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                ct);
            var current = await db.ExternalAuthAttempts.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == attempt.Id, ct);
            if (current.Status != ExternalAuthStatuses.Completed
                || current.ConsumedAt is not null
                || current.ExpiresAt <= now
                || !HasCandidate(current)
                || !SecretMatches(request.ExchangeCode, current.ExchangeCodeHash))
            {
                await transaction.RollbackAsync(ct);
                return new ExternalExchangeOutcome(null, ExternalAuthErrors.CodeConsumed);
            }

            var consumed = await db.ExternalAuthAttempts
                .Where(a => a.Id == attempt.Id
                    && a.Status == ExternalAuthStatuses.Completed
                    && a.ConsumedAt == null
                    && a.ExpiresAt > now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(a => a.Status, ExternalAuthStatuses.Consumed)
                    .SetProperty(a => a.ConsumedAt, now), ct);
            if (consumed != 1)
            {
                await transaction.RollbackAsync(ct);
                return new ExternalExchangeOutcome(null, ExternalAuthErrors.CodeConsumed);
            }

            var user = await accounts.FindByExternalLoginAsync(
                current.Provider,
                current.CandidateProviderKey!,
                ct);
            if (user is null)
            {
                if (await users.FindByEmailAsync(current.CandidateEmail!) is not null)
                {
                    await transaction.RollbackAsync(ct);
                    return new ExternalExchangeOutcome(null, ExternalAuthErrors.AccountLinkRequired);
                }

                var creation = await accounts.CreateAsync(
                    current.CandidateEmail!,
                    current.CandidateDisplayName!,
                    externalLogin: Mapping(current),
                    ct: ct);
                if (!creation.Succeeded)
                {
                    await transaction.RollbackAsync(ct);
                    return new ExternalExchangeOutcome(null, ExternalAuthErrors.AccountLinkRequired);
                }
                user = creation.User;
            }

            var resolvedUser = user
                ?? throw new InvalidOperationException("External account creation returned no user.");
            await UpdateAvatarAsync(db, resolvedUser.Id, current, ct);
            var tokens = await tokenIssuer.IssueAsync(resolvedUser, Guid.NewGuid(), now, ct);
            await db.ExternalAuthAttempts.Where(candidate => candidate.Id == current.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.UserId, resolvedUser.Id), ct);
            db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                TenantId = resolvedUser.TenantId,
                UserId = resolvedUser.Id,
                Action = "auth.external.exchanged",
                Detail = $"provider={attempt.Provider}",
                OccurredAt = now,
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new ExternalExchangeOutcome(tokens, null);
            });

        if (outcome.Tokens is null)
        {
            if (outcome.ErrorCode == ExternalAuthErrors.CodeConsumed)
            {
                await AuditReplayAsync(db, users, attempt, now, ct);
            }
            return Error(StatusCodes.Status409Conflict, outcome.ErrorCode!);
        }

        return Results.Ok(outcome.Tokens);
    }

    private static async Task<IResult> LinkExchangeAsync(
        ExternalAuthExchangeRequest request,
        ClaimsPrincipal principal,
        CastmillDbContext db,
        UserManager<CastmillUser> users,
        IAccountService accounts,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = AuthEndpoints.GetUserId(principal);
        var attempt = request.AttemptId == Guid.Empty
            ? null
            : await db.ExternalAuthAttempts.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == request.AttemptId, ct);
        if (attempt is null
            || !SecretMatches(request.ExchangeCode, attempt.ExchangeCodeHash)
            || !IsValidCodeVerifier(request.CodeVerifier)
            || !CodeVerifierMatches(request.CodeVerifier, attempt.CodeChallenge))
        {
            return Results.Unauthorized();
        }
        if (attempt.LinkUserId != userId)
        {
            return Error(StatusCodes.Status403Forbidden, ExternalAuthErrors.ExchangeNotAllowed);
        }
        if (attempt.ExpiresAt <= clock.GetUtcNow())
        {
            return Error(StatusCodes.Status410Gone, ExternalAuthErrors.AttemptExpired);
        }
        if (attempt.Status != ExternalAuthStatuses.Completed || !HasCandidate(attempt))
        {
            return Error(StatusCodes.Status409Conflict,
                attempt.ErrorCode ?? ExternalAuthErrors.AttemptPending);
        }

        var now = clock.GetUtcNow();
        var strategy = db.Database.CreateExecutionStrategy();
        var errorCode = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                ct);
            var current = await db.ExternalAuthAttempts.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == attempt.Id, ct);
            if (current.Status != ExternalAuthStatuses.Completed
                || current.ConsumedAt is not null
                || current.LinkUserId != userId
                || !SecretMatches(request.ExchangeCode, current.ExchangeCodeHash))
            {
                await transaction.RollbackAsync(ct);
                return ExternalAuthErrors.CodeConsumed;
            }

            var consumed = await db.ExternalAuthAttempts
                .Where(candidate => candidate.Id == current.Id
                    && candidate.Status == ExternalAuthStatuses.Completed
                    && candidate.ConsumedAt == null
                    && candidate.ExpiresAt > now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.Status, ExternalAuthStatuses.Consumed)
                    .SetProperty(candidate => candidate.ConsumedAt, now)
                    .SetProperty(candidate => candidate.UserId, userId), ct);
            if (consumed != 1)
            {
                await transaction.RollbackAsync(ct);
                return ExternalAuthErrors.CodeConsumed;
            }

            var user = await users.FindByIdAsync(userId.ToString());
            var mapped = await accounts.FindByExternalLoginAsync(
                current.Provider,
                current.CandidateProviderKey!,
                ct);
            if (user is null || (mapped is not null && mapped.Id != userId))
            {
                await transaction.RollbackAsync(ct);
                return ExternalAuthErrors.LoginAlreadyAssociated;
            }
            var logins = await users.GetLoginsAsync(user);
            if (logins.Any(login => login.LoginProvider == current.Provider))
            {
                await transaction.RollbackAsync(ct);
                return ExternalAuthErrors.LoginAlreadyAssociated;
            }

            var link = await accounts.LinkExternalLoginAsync(user, Mapping(current), ct);
            if (!link.Succeeded)
            {
                await transaction.RollbackAsync(ct);
                return ExternalAuthErrors.LoginAlreadyAssociated;
            }
            await UpdateAvatarAsync(db, user.Id, current, ct);
            db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                TenantId = user.TenantId,
                UserId = user.Id,
                Action = "auth.external.linked",
                Detail = $"provider={current.Provider}",
                OccurredAt = now,
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return null;
        });
        return errorCode is null
            ? Results.NoContent()
            : Error(StatusCodes.Status409Conflict, errorCode);
    }

    private static async Task<IResult> UnlinkAsync(
        string provider,
        ClaimsPrincipal principal,
        UserManager<CastmillUser> users,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (provider is not ExternalAuthProviders.Microsoft and not ExternalAuthProviders.Google)
        {
            return Error(StatusCodes.Status400BadRequest, ExternalAuthErrors.InvalidProvider);
        }

        var outcome = StatusCodes.Status204NoContent;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                ct);
            var user = await users.FindByIdAsync(AuthEndpoints.GetUserId(principal).ToString());
            if (user is null)
            {
                outcome = StatusCodes.Status401Unauthorized;
                await transaction.RollbackAsync(ct);
                return;
            }

            var logins = await users.GetLoginsAsync(user);
            var providerLogins = logins
                .Where(candidate => candidate.LoginProvider == provider)
                .ToArray();
            if (providerLogins.Length == 0)
            {
                outcome = StatusCodes.Status404NotFound;
                await transaction.RollbackAsync(ct);
                return;
            }

            if (!await users.HasPasswordAsync(user)
                && !logins.Any(candidate => candidate.LoginProvider != provider))
            {
                outcome = StatusCodes.Status409Conflict;
                await transaction.RollbackAsync(ct);
                return;
            }

            foreach (var login in providerLogins)
            {
                var removal = await users.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey);
                if (!removal.Succeeded)
                {
                    throw new InvalidOperationException("External login removal failed.");
                }
            }

            db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                TenantId = user.TenantId,
                UserId = user.Id,
                Action = "auth.external.unlinked",
                Detail = $"provider={provider}",
                OccurredAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });

        return outcome switch
        {
            StatusCodes.Status204NoContent => Results.NoContent(),
            StatusCodes.Status401Unauthorized => Results.Unauthorized(),
            StatusCodes.Status404NotFound =>
                Error(StatusCodes.Status404NotFound, ExternalAuthErrors.LoginNotLinked),
            _ => Error(StatusCodes.Status409Conflict, ExternalAuthErrors.LastLoginMethod),
        };
    }

    private static Task UpdateAvatarAsync(
        CastmillDbContext db,
        Guid userId,
        ExternalAuthAttempt attempt,
        CancellationToken ct)
    {
        if (attempt.CandidateAvatarImage is not { Length: > 0 } image
            || string.IsNullOrWhiteSpace(attempt.CandidateAvatarContentType))
        {
            return Task.CompletedTask;
        }

        return db.Users
            .Where(user => user.Id == userId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(user => user.AvatarImage, image)
                .SetProperty(user => user.AvatarContentType, attempt.CandidateAvatarContentType), ct);
    }

    private static ExternalAuthProviderCredentials? CredentialsFor(
        string provider,
        ExternalAuthOptions options) => provider switch
    {
        ExternalAuthProviders.Microsoft => options.Providers.Microsoft,
        ExternalAuthProviders.Google => options.Providers.Google,
        _ => null,
    };

    private static bool SecretMatches(string supplied, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(supplied) || string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool IsValidCodeVerifier(string verifier) =>
        verifier.Length is >= 43 and <= 128
        && verifier.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~');

    private static bool CodeVerifierMatches(string verifier, string expectedChallenge)
    {
        var actual = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var expected = Encoding.ASCII.GetBytes(expectedChallenge);
        var encoded = Encoding.ASCII.GetBytes(WebEncoders.Base64UrlEncode(actual));
        return expected.Length == encoded.Length
            && CryptographicOperations.FixedTimeEquals(encoded, expected);
    }

    private static bool HasCandidate(ExternalAuthAttempt attempt) =>
        !string.IsNullOrWhiteSpace(attempt.CandidateProviderKey)
        && !string.IsNullOrWhiteSpace(attempt.CandidateEmail)
        && !string.IsNullOrWhiteSpace(attempt.CandidateDisplayName);

    private static ExternalLoginMapping Mapping(ExternalAuthAttempt attempt) => new(
        attempt.Provider,
        attempt.CandidateProviderKey!,
        attempt.Provider == ExternalAuthProviders.Microsoft ? "Microsoft" : "Google");

    private sealed record ExternalExchangeOutcome(AuthResponse? Tokens, string? ErrorCode);

    internal static Task<T> ExecuteOutcomeAsync<T>(
        Func<Func<Task<T>>, Task<T>> execute,
        Func<Task<T>> operation) => execute(operation);

    private static Task<int> MarkExpiredAsync(CastmillDbContext db, Guid attemptId, CancellationToken ct) =>
        MarkFailedAsync(db, attemptId, ExternalAuthErrors.AttemptExpired, ct);

    private static Task<int> MarkFailedAsync(
        CastmillDbContext db,
        Guid attemptId,
        string errorCode,
        CancellationToken ct) =>
        db.ExternalAuthAttempts
            .Where(a => a.Id == attemptId
                && (a.Status == ExternalAuthStatuses.Pending
                    || a.Status == ExternalAuthStatuses.Completing
                    || a.Status == ExternalAuthStatuses.Completed))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.Status, ExternalAuthStatuses.Failed)
                .SetProperty(a => a.ErrorCode, errorCode), ct);

    private static async Task AuditReplayAsync(
        CastmillDbContext db,
        UserManager<CastmillUser> users,
        ExternalAuthAttempt attempt,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (attempt.UserId is not { } userId)
        {
            return;
        }

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return;
        }

        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = user.TenantId,
            UserId = user.Id,
            Action = "auth.external.exchange-replay",
            Detail = $"provider={attempt.Provider}",
            OccurredAt = now,
        });
        await db.SaveChangesAsync(ct);
    }

    private static IResult Error(int statusCode, string errorCode) =>
        Results.Json(new { errorCode }, statusCode: statusCode);
}