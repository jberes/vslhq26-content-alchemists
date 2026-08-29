using System.Security.Claims;
using System.Security.Cryptography;
using Castmill.Api.Data;
using Castmill.Core.Auth;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Castmill.Api.Auth;

public sealed record ExternalAuthCompletionResult(
    bool Succeeded,
    string? ErrorCode,
    string? ExchangeCode = null);

public interface IExternalAuthCompletionService
{
    Task<ExternalAuthCompletionResult> CompleteAsync(
        Guid attemptId,
        ClaimsPrincipal syntheticPrincipal,
        CancellationToken ct = default);

    Task FailAsync(Guid attemptId, string errorCode, CancellationToken ct = default);
}

public sealed class ExternalAuthCompletionService(
    CastmillDbContext db,
    IExternalIdentityResolver identities,
    TimeProvider clock) : IExternalAuthCompletionService
{
    public async Task<ExternalAuthCompletionResult> CompleteAsync(
        Guid attemptId,
        ClaimsPrincipal syntheticPrincipal,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var attempt = await db.ExternalAuthAttempts.SingleOrDefaultAsync(a => a.Id == attemptId, ct);
        if (attempt is null)
        {
            return new(false, ExternalAuthErrors.AttemptNotFound);
        }

        if (attempt.ExpiresAt <= now)
        {
            return await FailTrackedAsync(attempt, ExternalAuthErrors.AttemptExpired, now, ct);
        }

        if (attempt.Status != ExternalAuthStatuses.Pending)
        {
            return new(false, attempt.ErrorCode ?? ExternalAuthErrors.AttemptFailed);
        }

        ExternalIdentity identity;
        try
        {
            identity = identities.Resolve(attempt.Provider, syntheticPrincipal);
        }
        catch (ExternalIdentityException exception)
        {
            return await FailTrackedAsync(attempt, exception.ErrorCode, now, ct);
        }
        catch (SecurityTokenInvalidIssuerException)
        {
            return await FailTrackedAsync(
                attempt,
                ExternalAuthErrors.InvalidProviderIdentity,
                now,
                ct);
        }

        var exchangeCode = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var completed = await db.ExternalAuthAttempts
            .Where(a => a.Id == attempt.Id
                && a.Status == ExternalAuthStatuses.Pending
                && a.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.CandidateProviderKey, identity.ProviderKey)
                .SetProperty(a => a.CandidateEmail, identity.Email)
                .SetProperty(a => a.CandidateDisplayName, identity.DisplayName)
                .SetProperty(a => a.ExchangeCodeHash, ExternalAuthEndpoints.HashSecret(exchangeCode))
                .SetProperty(a => a.Status, ExternalAuthStatuses.Completed)
                .SetProperty(a => a.CompletedAt, now), ct);
        return completed == 1
            ? new(true, null, exchangeCode)
            : new(false, ExternalAuthErrors.AttemptFailed);
    }

    public Task FailAsync(Guid attemptId, string errorCode, CancellationToken ct = default) =>
        FailByIdAsync(attemptId, errorCode, includeCompleting: false, ct);

    private async Task<ExternalAuthCompletionResult> FailTrackedAsync(
        ExternalAuthAttempt attempt,
        string errorCode,
        DateTimeOffset now,
        CancellationToken ct)
    {
        attempt.Status = ExternalAuthStatuses.Failed;
        attempt.ErrorCode = errorCode;
        await db.SaveChangesAsync(ct);
        return new(false, errorCode);
    }

    private async Task<ExternalAuthCompletionResult> FailByIdAsync(
        Guid attemptId,
        string errorCode,
        bool includeCompleting,
        CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await db.ExternalAuthAttempts
            .Where(a => a.Id == attemptId
                && (a.Status == ExternalAuthStatuses.Pending
                    || (includeCompleting && a.Status == ExternalAuthStatuses.Completing)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.Status, ExternalAuthStatuses.Failed)
                .SetProperty(a => a.ErrorCode, errorCode), ct);
        return new(false, errorCode);
    }
}