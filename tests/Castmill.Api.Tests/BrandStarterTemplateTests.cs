using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Core;
using Castmill.Core.Auth;
using Castmill.Core.Resources;

namespace Castmill.Api.Tests;

/// <summary>
/// A brand starts with the authored content briefs (ADR-069). The template is the primary
/// instruction every generation reads, so a brand created and used the same afternoon was
/// running on Castmill's generic guidance alone.
/// </summary>
[Collection("api")]
public sealed class BrandStarterTemplateTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task A_new_brand_is_seeded_with_the_starter_templates()
    {
        var client = factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"starter-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Owner"));
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);

        var created = await client.PostAsJsonAsync("/api/v1/brands", new BrandProfileUpsertRequest("Ignite UI", null));
        created.EnsureSuccessStatusCode();
        var brand = (await created.Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!;

        var templates = await client.GetFromJsonAsync<List<BrandTemplateResponse>>($"/api/v1/brands/{brand.Id}/templates");

        Assert.NotNull(templates);
        var blog = templates!.Single(t => t.Kind == "blog");
        Assert.True(blog.IsDefault);
        // The robust brief, not the five-line one it replaced.
        Assert.Contains("the first 90 words decide everything", blog.SteeringPrompt, StringComparison.Ordinal);
        Assert.Contains("1600-2400 words", blog.SteeringPrompt, StringComparison.Ordinal);
        Assert.Contains("REJECT YOUR OWN DRAFT IF", blog.SteeringPrompt, StringComparison.Ordinal);
        Assert.Contains(templates, t => t.Kind == "youtube");
    }

    [Fact]
    public void Every_seeded_kind_actually_has_a_starter()
    {
        Assert.NotEmpty(BrandTemplateStarters.SeededKinds);
        Assert.All(BrandTemplateStarters.SeededKinds,
            kind => Assert.False(string.IsNullOrWhiteSpace(BrandTemplateStarters.For(kind))));
    }
}
