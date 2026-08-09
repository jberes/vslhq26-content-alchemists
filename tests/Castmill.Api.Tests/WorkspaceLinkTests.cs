using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.Api.Tests;

/// <summary>
/// The YouTube description carries a link block, and the model must never write those URLs.
/// It is asked to leave a <c>{{LINKS}}</c> placeholder and the real links are substituted
/// after generation, so a hallucinated link is impossible by construction rather than by
/// instruction — the only kind of guarantee worth having about a URL someone will publish.
/// </summary>
[Collection("api")]
public sealed class WorkspaceLinkTests(CastmillApiFactory factory)
{
    private async Task<(HttpClient Client, Guid UserId, Guid TenantId)> AuthedAsync()
    {
        var client = factory.CreateClient();
        var email = $"links-{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "correct-horse-battery-staple", "Link Tester"));
        response.EnsureSuccessStatusCode();

        var tokens = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        using var scope = factory.CreateDbScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<CastmillDbContext>>();
        await using var db = new CastmillDbContext(options, new NullTenantProvider());
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == email);

        return (client, user.Id, user.TenantId);
    }

    // The tenant is supplied for real: UserSettings is tenant-scoped, so a null-tenant context
    // filters every row away and the service would look broken when it is not.
    private async Task<string> BlockAsync(Guid userId, Guid tenantId)
    {
        using var scope = factory.CreateDbScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<CastmillDbContext>>();
        await using var db = new CastmillDbContext(options, new FixedTenantProvider(tenantId));
        return await new WorkspaceLinks(db).RenderBlockAsync(userId, CancellationToken.None);
    }

    private async Task<IReadOnlyList<WorkspaceLink>> LinksAsync(Guid userId, Guid tenantId)
    {
        using var scope = factory.CreateDbScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<CastmillDbContext>>();
        await using var db = new CastmillDbContext(options, new FixedTenantProvider(tenantId));
        return await new WorkspaceLinks(db).GetAsync(userId, CancellationToken.None);
    }

    [Fact]
    public async Task Configured_links_render_as_a_labelled_block()
    {
        var (client, userId, tenantId) = await AuthedAsync();

        await client.PutAsJsonAsync(
            $"/api/v1/settings/{WorkspaceLinks.SettingKey}",
            new SettingWriteRequest("""
                [{"label":"Website","url":"https://acme.example"},
                 {"label":"LinkedIn","url":"https://linkedin.com/company/acme"}]
                """));

        var block = (await BlockAsync(userId, tenantId)).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(
            "Website: https://acme.example\nLinkedIn: https://linkedin.com/company/acme",
            block);
    }

    [Fact]
    public async Task No_links_configured_renders_nothing_rather_than_an_empty_heading()
    {
        var (_, userId, tenantId) = await AuthedAsync();

        // Empty, not a dangling "Links:" — the placeholder is removed, and a heading with
        // nothing under it would be published verbatim.
        Assert.Equal(string.Empty, await BlockAsync(userId, tenantId));
    }

    [Fact]
    public async Task Malformed_stored_json_does_not_take_a_generation_run_down()
    {
        var (client, userId, tenantId) = await AuthedAsync();

        await client.PutAsJsonAsync(
            $"/api/v1/settings/{WorkspaceLinks.SettingKey}",
            new SettingWriteRequest("this is not json"));

        Assert.Empty(await LinksAsync(userId, tenantId));
    }

    [Fact]
    public async Task Rows_missing_a_label_or_a_url_are_dropped()
    {
        var (client, userId, tenantId) = await AuthedAsync();

        await client.PutAsJsonAsync(
            $"/api/v1/settings/{WorkspaceLinks.SettingKey}",
            new SettingWriteRequest("""
                [{"label":"Website","url":"https://acme.example"},
                 {"label":"Half done","url":""},
                 {"label":"","url":"https://orphan.example"}]
                """));

        // A half-filled row would render as a dangling label in a published description.
        var only = Assert.Single(await LinksAsync(userId, tenantId));
        Assert.Equal("Website", only.Label);
    }

    /// <summary>
    /// The links live in the PLAINTEXT settings store on purpose — they are public URLs. This
    /// pins that the reserved secret prefix is still refused, so the two stores cannot be
    /// confused for one another.
    /// </summary>
    [Fact]
    public async Task The_links_key_is_plaintext_and_the_secret_prefix_is_still_refused()
    {
        var (client, _, _) = await AuthedAsync();

        Assert.False(WorkspaceLinks.SettingKey.StartsWith("secret.", StringComparison.OrdinalIgnoreCase));

        var refused = await client.PutAsJsonAsync(
            "/api/v1/settings/secret.workspace.links", new SettingWriteRequest("[]"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    /// <summary>Bypasses the tenant filter to look the user up by email.</summary>
    private sealed class NullTenantProvider : Castmill.Api.Tenancy.ITenantProvider
    {
        public Guid? TenantId => null;
    }

    /// <summary>The real tenant, so tenant-scoped rows are visible exactly as they are in the app.</summary>
    private sealed class FixedTenantProvider(Guid tenantId) : Castmill.Api.Tenancy.ITenantProvider
    {
        public Guid? TenantId => tenantId;
    }
}
