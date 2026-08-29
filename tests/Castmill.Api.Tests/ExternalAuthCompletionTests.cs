using System.Security.Claims;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Core.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class ExternalAuthCompletionTests(CastmillApiFactory factory)
{
    [Fact]
    public void Microsoft_issuer_rejects_non_login_host()
    {
        Assert.Throws<SecurityTokenInvalidIssuerException>(() =>
            ExternalIdentityResolver.ValidateMicrosoftIssuer(
                $"https://example.com/{Guid.NewGuid():D}/v2.0",
                null!,
                null!));
    }

    [Fact]
    public async Task Microsoft_identity_uses_tenant_and_object_ids_as_immutable_key()
    {
        var tenantId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var email = $"microsoft-{Guid.NewGuid():N}@example.com";
        var attemptId = await AddAttemptAsync(ExternalAuthProviders.Microsoft);

        var result = await CompleteAsync(
            attemptId,
            Principal(
                (ExternalIdentityResolver.ValidatedIssuerClaimType,
                    $"https://login.microsoftonline.com/{tenantId:D}/v2.0"),
                ("tid", tenantId.ToString("D")),
                ("oid", objectId.ToString("D")),
                ("email", email),
                ("name", "Microsoft Tester")));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.ExchangeCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        var attempt = await db.ExternalAuthAttempts.SingleAsync(candidate => candidate.Id == attemptId);
        Assert.Equal($"{tenantId:N}:{objectId:N}", attempt.CandidateProviderKey);
        Assert.Equal(email, attempt.CandidateEmail);
        Assert.Equal(ExternalAuthEndpoints.HashSecret(result.ExchangeCode), attempt.ExchangeCodeHash);
        Assert.False(await db.Users.AnyAsync(candidate => candidate.Email == email));
    }

    [Fact]
    public async Task Microsoft_work_account_uses_preferred_username_only_as_contact_metadata()
    {
        var tenantId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var preferredUsername = $"work-{Guid.NewGuid():N}@example.com";
        var attemptId = await AddAttemptAsync(ExternalAuthProviders.Microsoft);

        var result = await CompleteAsync(
            attemptId,
            Principal(
                (ExternalIdentityResolver.ValidatedIssuerClaimType,
                    $"https://login.microsoftonline.com/{tenantId:D}/v2.0"),
                ("tid", tenantId.ToString("D")),
                ("oid", objectId.ToString("D")),
                ("preferred_username", preferredUsername),
                ("name", "Microsoft Work Tester")));

        Assert.True(result.Succeeded);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        var attempt = await db.ExternalAuthAttempts.SingleAsync(candidate => candidate.Id == attemptId);
        Assert.Equal($"{tenantId:N}:{objectId:N}", attempt.CandidateProviderKey);
        Assert.Equal(preferredUsername, attempt.CandidateEmail);
        Assert.False(await db.Users.AnyAsync(candidate => candidate.Email == preferredUsername));
    }

    [Fact]
    public async Task Microsoft_issuer_tenant_must_match_tid()
    {
        var attemptId = await AddAttemptAsync(ExternalAuthProviders.Microsoft);
        var result = await CompleteAsync(
            attemptId,
            Principal(
                (ExternalIdentityResolver.ValidatedIssuerClaimType,
                    $"https://login.microsoftonline.com/{Guid.NewGuid():D}/v2.0"),
                ("tid", Guid.NewGuid().ToString("D")),
                ("oid", Guid.NewGuid().ToString("D")),
                ("email", $"mismatch-{Guid.NewGuid():N}@example.com")));

        Assert.False(result.Succeeded);
        Assert.Equal(ExternalAuthErrors.InvalidProviderIdentity, result.ErrorCode);
        await AssertFailedAsync(attemptId, ExternalAuthErrors.InvalidProviderIdentity);
    }

    [Fact]
    public async Task Google_requires_a_verified_usable_email()
    {
        var attemptId = await AddAttemptAsync(ExternalAuthProviders.Google);
        var result = await CompleteAsync(
            attemptId,
            GooglePrincipal($"unverified-{Guid.NewGuid():N}@example.com", verified: false));

        Assert.False(result.Succeeded);
        Assert.Equal(ExternalAuthErrors.ExternalEmailRequired, result.ErrorCode);
        await AssertFailedAsync(attemptId, ExternalAuthErrors.ExternalEmailRequired);
    }

    [Fact]
    public async Task Existing_provider_mapping_is_not_resolved_by_callback()
    {
        var providerKey = $"existing-{Guid.NewGuid():N}";
        var user = await CreateUserAsync(
            new ExternalLoginMapping(ExternalAuthProviders.Google, providerKey, "Google"));
        var attemptId = await AddAttemptAsync(ExternalAuthProviders.Google);

        var result = await CompleteAsync(
            attemptId,
            GooglePrincipal($"different-{Guid.NewGuid():N}@example.com", subject: providerKey));

        Assert.True(result.Succeeded);
        await using var scope = factory.Services.CreateAsyncScope();
        var attempt = await scope.ServiceProvider.GetRequiredService<CastmillDbContext>()
            .ExternalAuthAttempts.SingleAsync(candidate => candidate.Id == attemptId);
        Assert.Null(attempt.UserId);
        Assert.Equal(providerKey, attempt.CandidateProviderKey);
        Assert.NotNull(result.ExchangeCode);
        _ = user;
    }

    [Fact]
    public async Task New_external_identity_does_not_create_account_before_exchange()
    {
        var email = $"new-google-{Guid.NewGuid():N}@example.com";
        var providerKey = $"new-{Guid.NewGuid():N}";
        var attemptId = await AddAttemptAsync(ExternalAuthProviders.Google);

        var result = await CompleteAsync(
            attemptId,
            GooglePrincipal(email, subject: providerKey));

        Assert.True(result.Succeeded);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        Assert.False(await db.Users.AnyAsync(candidate => candidate.Email == email));
        Assert.False(await db.UserLogins.AnyAsync(candidate =>
            candidate.LoginProvider == ExternalAuthProviders.Google
            && candidate.ProviderKey == providerKey));
        var attempt = await db.ExternalAuthAttempts.SingleAsync(candidate => candidate.Id == attemptId);
        Assert.Equal(email, attempt.CandidateEmail);
        Assert.Equal(providerKey, attempt.CandidateProviderKey);
    }

    [Fact]
    public async Task Existing_email_without_mapping_requires_explicit_link()
    {
        var user = await CreateUserAsync();
        var attemptId = await AddAttemptAsync(ExternalAuthProviders.Google);

        var result = await CompleteAsync(
            attemptId,
            GooglePrincipal(user.Email!, subject: $"collision-{Guid.NewGuid():N}"));

        Assert.True(result.Succeeded);
        await using var scope = factory.Services.CreateAsyncScope();
        var attempt = await scope.ServiceProvider.GetRequiredService<CastmillDbContext>()
            .ExternalAuthAttempts.SingleAsync(candidate => candidate.Id == attemptId);
        Assert.Equal(user.Email, attempt.CandidateEmail);
        Assert.Null(attempt.UserId);
    }

    [Fact]
    public async Task Link_attempt_cannot_move_a_mapping_owned_by_another_user()
    {
        var providerKey = $"owned-{Guid.NewGuid():N}";
        var owner = await CreateUserAsync(
            new ExternalLoginMapping(ExternalAuthProviders.Google, providerKey, "Google"));
        var target = await CreateUserAsync();
        var attemptId = await AddAttemptAsync(ExternalAuthProviders.Google, target.Id);

        var result = await CompleteAsync(
            attemptId,
            GooglePrincipal(owner.Email!, subject: providerKey));

        Assert.True(result.Succeeded);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        Assert.Equal(owner.Id, (await db.UserLogins.SingleAsync(candidate =>
            candidate.LoginProvider == ExternalAuthProviders.Google
            && candidate.ProviderKey == providerKey)).UserId);
        Assert.False(await db.UserLogins.AnyAsync(candidate => candidate.UserId == target.Id));
    }

    [Fact]
    public async Task Concurrent_completion_allows_only_one_callback_to_persist_proof()
    {
        var email = $"concurrent-{Guid.NewGuid():N}@example.com";
        var subject = $"concurrent-{Guid.NewGuid():N}";
        var attemptId = await AddAttemptAsync(ExternalAuthProviders.Google);

        var results = await Task.WhenAll(
            CompleteAsync(attemptId, GooglePrincipal(email, subject: subject)),
            CompleteAsync(attemptId, GooglePrincipal(email, subject: subject)));

        Assert.Single(results, result => result.Succeeded);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        Assert.Equal(0, await db.Users.CountAsync(user => user.Email == email));
        var attempt = await db.ExternalAuthAttempts.SingleAsync(candidate => candidate.Id == attemptId);
        Assert.Equal(subject, attempt.CandidateProviderKey);
        Assert.NotNull(attempt.ExchangeCodeHash);
    }

    [Fact]
    public async Task Link_attempt_rejects_a_second_key_for_the_same_provider()
    {
        var user = await CreateUserAsync(new ExternalLoginMapping(
            ExternalAuthProviders.Google,
            $"first-{Guid.NewGuid():N}",
            "Google"));
        var attemptId = await AddAttemptAsync(ExternalAuthProviders.Google, user.Id);

        var result = await CompleteAsync(
            attemptId,
            GooglePrincipal(user.Email!, subject: $"second-{Guid.NewGuid():N}"));

        Assert.True(result.Succeeded);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        Assert.Equal(1, await db.UserLogins.CountAsync(login =>
            login.UserId == user.Id
            && login.LoginProvider == ExternalAuthProviders.Google));
    }

    private async Task<Guid> AddAttemptAsync(string provider, Guid? linkUserId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var now = DateTimeOffset.UtcNow;
        var attempt = new ExternalAuthAttempt
        {
            Id = Guid.NewGuid(),
            Provider = provider,
            ClientKind = ExternalAuthClientKinds.Desktop,
            ReturnRouteKey = ExternalAuthReturnRoutes.SignIn,
            CodeChallenge = new string('a', 43),
            PollSecretHash = ExternalAuthEndpoints.HashSecret($"poll-{Guid.NewGuid():N}"),
            ExchangeCodeHash = ExternalAuthEndpoints.HashSecret($"exchange-{Guid.NewGuid():N}"),
            Status = ExternalAuthStatuses.Pending,
            LinkUserId = linkUserId,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(10),
        };
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        db.ExternalAuthAttempts.Add(attempt);
        await db.SaveChangesAsync();
        return attempt.Id;
    }

    private async Task<ExternalAuthCompletionResult> CompleteAsync(
        Guid attemptId,
        ClaimsPrincipal syntheticPrincipal)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IExternalAuthCompletionService>()
            .CompleteAsync(attemptId, syntheticPrincipal);
    }

    private async Task<CastmillUser> CreateUserAsync(ExternalLoginMapping? mapping = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IAccountService>().CreateAsync(
            $"completion-{Guid.NewGuid():N}@example.com",
            "Completion Tester",
            "correct-horse-battery-staple",
            mapping);
        Assert.True(result.Succeeded);
        return result.User!;
    }

    private async Task AssertFailedAsync(Guid attemptId, string errorCode)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var attempt = await scope.ServiceProvider.GetRequiredService<CastmillDbContext>()
            .ExternalAuthAttempts.AsNoTracking().SingleAsync(candidate => candidate.Id == attemptId);
        Assert.Equal(ExternalAuthStatuses.Failed, attempt.Status);
        Assert.Equal(errorCode, attempt.ErrorCode);
    }

    private static ClaimsPrincipal GooglePrincipal(
        string email,
        bool verified = true,
        string? subject = null) => Principal(
        (ExternalIdentityResolver.ValidatedIssuerClaimType, "https://accounts.google.com"),
        ("sub", subject ?? $"subject-{Guid.NewGuid():N}"),
        ("email", email),
        ("email_verified", verified ? "true" : "false"),
        ("name", "Google Tester"));

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(
            claims.Select(claim => new Claim(claim.Type, claim.Value)),
            "synthetic-provider-callback"));
}