using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Services.Secrets;
using Castmill.Core.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.Api.Tests;

/// <summary>
/// A pasted credential is trimmed before it is stored (ADR-073).
///
/// Reported 2026-09-07: "API key not valid. Please pass a valid API key" from Gemini after
/// re-entering the key. The stored secret was 50 characters where an AI Studio key is 39 —
/// the value went to the provider exactly as pasted, trailing newline and all, and the
/// provider's answer reads as a wrong key rather than a copy-paste artefact.
/// </summary>
[Collection("api")]
public sealed class SecretTrimTests(CastmillApiFactory factory)
{
    [Theory]
    [InlineData("  AIzaSyTest-key-value  ")]
    [InlineData("AIzaSyTest-key-value\n")]
    [InlineData("\r\nAIzaSyTest-key-value\r\n")]
    [InlineData("\tAIzaSyTest-key-value ")]
    public async Task Whitespace_around_a_pasted_key_never_reaches_the_provider(string pasted)
    {
        var client = factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"trim-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Owner"));
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);

        var set = await client.PutAsJsonAsync("/api/v1/settings/secrets/NanoBananaKey", new SecretBody(pasted));
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        var me = (await client.GetFromJsonAsync<MeResponse>("/api/v1/me"))!;
        using var scope = factory.Services.CreateScope();
        var secrets = scope.ServiceProvider.GetRequiredService<IUserSecretsService>();
        // Read back through the tenant the write happened under.
        var stored = await ReadAsync(scope, me.UserId, me.TenantId);

        Assert.Equal("AIzaSyTest-key-value", stored);
    }

    private static async Task<string?> ReadAsync(IServiceScope scope, Guid userId, Guid tenantId)
    {
        var options = scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.DbContextOptions<Castmill.Api.Data.CastmillDbContext>>();
        var cipher = scope.ServiceProvider.GetRequiredService<ISecretCipher>();
        await using var db = new Castmill.Api.Data.CastmillDbContext(options, new TrimTenantProvider());
        var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(db.UserSettings),
            s => s.UserId == userId && s.TenantId == tenantId && s.Key == "secret.NanoBananaKey");
        return cipher.Decrypt(row.Value);
    }

    private sealed record SecretBody(string Value);

    private sealed class TrimTenantProvider : Castmill.Api.Tenancy.ITenantProvider
    {
        public Guid? TenantId => null;
    }
}
