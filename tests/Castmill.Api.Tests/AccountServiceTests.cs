using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Core.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class AccountServiceTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task Registration_still_creates_one_tenant_and_password_user()
    {
        var email = $"register-{Guid.NewGuid():N}@example.com";
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(email, "correct-horse-battery-staple", "Registration Tester"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await response.Content.ReadFromJsonAsync<AuthResponse>());

        using var scope = factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        var user = await db.Users.SingleAsync(candidate => candidate.Email == email);
        Assert.True(await scope.ServiceProvider.GetRequiredService<UserManager<CastmillUser>>()
            .HasPasswordAsync(user));
        Assert.Equal(1, await db.Tenants.CountAsync(tenant => tenant.Id == user.TenantId));
    }

    [Fact]
    public async Task Passwordless_external_account_creates_one_tenant_user_and_mapping()
    {
        var email = $"external-{Guid.NewGuid():N}@example.com";
        var providerKey = $"subject-{Guid.NewGuid():N}";
        using var scope = factory.CreateDbScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();

        var result = await accounts.CreateAsync(
            email,
            "External Tester",
            externalLogin: new ExternalLoginMapping(
                ExternalAuthProviders.Google, providerKey, "Google"));

        Assert.True(result.Succeeded);
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        Assert.Equal(1, await db.Users.CountAsync(user => user.Email == email));
        Assert.Equal(1, await db.Tenants.CountAsync(tenant => tenant.Id == result.User!.TenantId));
        var login = await db.UserLogins.SingleAsync(candidate =>
            candidate.LoginProvider == ExternalAuthProviders.Google
            && candidate.ProviderKey == providerKey);
        Assert.Equal(result.User!.Id, login.UserId);
        Assert.False(await scope.ServiceProvider.GetRequiredService<UserManager<CastmillUser>>()
            .HasPasswordAsync(result.User));
    }

    [Fact]
    public async Task Duplicate_email_conflict_does_not_create_a_tenant()
    {
        var email = $"duplicate-{Guid.NewGuid():N}@example.com";
        var duplicateName = $"Duplicate {Guid.NewGuid():N}";
        using var scope = factory.CreateDbScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        var first = await accounts.CreateAsync(email, "Original", "correct-horse-battery-staple");
        Assert.True(first.Succeeded);

        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        var tenantCount = await db.Tenants.CountAsync();
        var duplicate = await accounts.CreateAsync(
            email,
            duplicateName,
            externalLogin: new ExternalLoginMapping(
                ExternalAuthProviders.Microsoft, $"subject-{Guid.NewGuid():N}", "Microsoft"));

        Assert.False(duplicate.Succeeded);
        Assert.Contains(duplicate.Result.Errors, error =>
            error.Code is "DuplicateEmail" or "DuplicateUserName");
        Assert.Equal(tenantCount, await db.Tenants.CountAsync());
        Assert.Equal(0, await db.Tenants.CountAsync(tenant => tenant.Name == duplicateName));
    }

    [Fact]
    public async Task Provider_key_lookup_returns_the_same_user_without_email_matching()
    {
        var providerKey = $"subject-{Guid.NewGuid():N}";
        using var scope = factory.CreateDbScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        var created = await accounts.CreateAsync(
            $"lookup-{Guid.NewGuid():N}@example.com",
            "Lookup Tester",
            externalLogin: new ExternalLoginMapping(
                ExternalAuthProviders.Google, providerKey, "Google"));
        Assert.True(created.Succeeded);

        var resolved = await accounts.FindByExternalLoginAsync(
            ExternalAuthProviders.Google, providerKey);
        var unknown = await accounts.FindByExternalLoginAsync(
            ExternalAuthProviders.Google, $"unknown-{Guid.NewGuid():N}");

        Assert.Equal(created.User!.Id, resolved!.Id);
        Assert.Null(unknown);
    }

    [Fact]
    public async Task Linking_an_existing_provider_key_to_another_user_is_rejected()
    {
        var mapping = new ExternalLoginMapping(
            ExternalAuthProviders.Microsoft, $"subject-{Guid.NewGuid():N}", "Microsoft");
        using var scope = factory.CreateDbScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        var first = await accounts.CreateAsync(
            $"link-a-{Guid.NewGuid():N}@example.com", "Link A", "correct-horse-battery-staple");
        var second = await accounts.CreateAsync(
            $"link-b-{Guid.NewGuid():N}@example.com", "Link B", "correct-horse-battery-staple");
        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);

        var firstLink = await accounts.LinkExternalLoginAsync(first.User!, mapping);
        var conflictingLink = await accounts.LinkExternalLoginAsync(second.User!, mapping);

        Assert.True(firstLink.Succeeded);
        Assert.False(conflictingLink.Succeeded);
        var resolved = await accounts.FindByExternalLoginAsync(mapping.LoginProvider, mapping.ProviderKey);
        Assert.Equal(first.User!.Id, resolved!.Id);
    }

    [Fact]
    public async Task External_only_account_change_password_returns_stable_problem()
    {
        var providerKey = $"subject-{Guid.NewGuid():N}";
        AuthResponse tokens;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
            var created = await accounts.CreateAsync(
                $"passwordless-{Guid.NewGuid():N}@example.com",
                "Passwordless Tester",
                externalLogin: new ExternalLoginMapping(
                    ExternalAuthProviders.Google, providerKey, "Google"));
            Assert.True(created.Succeeded);
            tokens = await scope.ServiceProvider.GetRequiredService<IAuthTokenIssuer>()
                .IssueAsync(
                    created.User!,
                    Guid.NewGuid(),
                    scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow());
        }

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/change-password")
        {
            Content = JsonContent.Create(new ChangePasswordRequest(
                "unused-current-password", "an-even-longer-new-password")),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("errors")
            .TryGetProperty(ExternalAuthErrors.PasswordNotConfigured, out _));
    }
}