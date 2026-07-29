using System.Net.Http.Json;
using Castmill.Api.Data;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.Api.Tests;

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<CastmillApiFactory>;

[Collection("api")]
public sealed class TenantIsolationTests(CastmillApiFactory factory)
{
    private sealed class FixedTenantProvider(Guid? tenantId) : ITenantProvider
    {
        public Guid? TenantId => tenantId;
    }

    private static CastmillDbContext CreateContextForTenant(IServiceScope scope, Guid? tenantId) =>
        new(scope.ServiceProvider.GetRequiredService<DbContextOptions<CastmillDbContext>>(),
            new FixedTenantProvider(tenantId));

    /// <summary>
    /// G1 check-in gate: cross-tenant reads fail structurally — the global query
    /// filter scopes every query to the caller's tenant, and an absent tenant
    /// (unauthenticated) sees nothing at all.
    /// </summary>
    [Fact]
    public async Task Campaigns_are_invisible_across_tenants()
    {
        var client = factory.CreateClient();

        async Task<MeResponse> RegisterAsync(string name)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/register",
                new RegisterRequest($"{name}-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", name));
            response.EnsureSuccessStatusCode();
            var tokens = await response.Content.ReadFromJsonAsync<AuthResponse>();
            using var me = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
            me.Headers.Authorization = new("Bearer", tokens!.AccessToken);
            var meResponse = await client.SendAsync(me);
            return (await meResponse.Content.ReadFromJsonAsync<MeResponse>())!;
        }

        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");

        using var scope = factory.CreateDbScope();

        // Seed a campaign in Alice's tenant.
        await using (var aliceDb = CreateContextForTenant(scope, alice.TenantId))
        {
            aliceDb.Campaigns.Add(new Campaign
            {
                Id = Guid.NewGuid(),
                TenantId = alice.TenantId,
                OwnerId = alice.UserId,
                Name = "Alice's launch",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await aliceDb.SaveChangesAsync();
        }

        await using (var aliceDb = CreateContextForTenant(scope, alice.TenantId))
        {
            Assert.Equal(1, await aliceDb.Campaigns.CountAsync());
        }

        // Bob's tenant sees nothing; no tenant sees nothing.
        await using (var bobDb = CreateContextForTenant(scope, bob.TenantId))
        {
            Assert.Equal(0, await bobDb.Campaigns.CountAsync());
        }

        await using (var anonDb = CreateContextForTenant(scope, null))
        {
            Assert.Equal(0, await anonDb.Campaigns.CountAsync());
        }
    }
}
