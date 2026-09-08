using System.Net.Http;
using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Layout;
using Castmill.UI.State;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// ADR-F69: the campaign header is the only way out of a view, so it renders no matter what
/// — an unreachable API used to throw out of persona loading and the ErrorBoundary replaced
/// the whole tab strip with one error line — and it names where the producer is.
/// </summary>
public sealed class CampaignHeaderNavigationTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("c4444444-1111-1111-1111-111111111111");
    private static readonly Guid BrandId = Guid.Parse("c4444444-1111-1111-1111-222222222222");
    private static readonly Guid ArtifactId = Guid.Parse("c4444444-1111-1111-1111-333333333333");
    private static readonly Guid SlotId = Guid.Parse("c4444444-1111-1111-1111-444444444444");

    public CampaignHeaderNavigationTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(),
            [new ArtifactPreviewResponse(ArtifactId, CampaignId, "blog", "Owning article", "Draft", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)],
            [new ImageSlotResponse(SlotId, CampaignId, "content-image", 1280, 720, null, null, null, null, SafeArea: true,
                "Empty", null, null, DateTimeOffset.UtcNow, ArtifactId: ArtifactId)],
            0, 6));
    }

    [Fact]
    public async Task The_tab_strip_and_back_button_survive_an_unreachable_api()
    {
        Http.OnThrow(HttpMethod.Get, $"api/v1/brands/{BrandId}", () => new HttpRequestException("Could not connect to the server."));

        var shell = RenderShell(CampaignView.Focus, $"campaigns/{CampaignId}/focus");

        await shell.WaitForAssertionAsync(() =>
            Assert.Equal(4, shell.FindAll(".cm-campaign-header .cm-tabs__tab").Count));
        Assert.DoesNotContain("The campaign header failed", shell.Markup, StringComparison.Ordinal);
        Assert.Single(shell.FindAll("button.cm-campaign-header__back"));
        Assert.Contains("Focus mode", shell.Find(".cm-campaign-header__trail").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_trail_names_the_item_an_open_image_belongs_to_and_links_back_to_it()
    {
        Http.OnThrow(HttpMethod.Get, $"api/v1/brands/{BrandId}", () => new HttpRequestException("offline"));

        var shell = RenderShell(CampaignView.ImageStudio, $"campaigns/{CampaignId}/images?slot={SlotId}");

        await shell.WaitForAssertionAsync(() =>
            Assert.Contains("Owning article", shell.Find(".cm-campaign-header__trail").TextContent, StringComparison.Ordinal));
        var trail = shell.Find(".cm-campaign-header__trail");
        Assert.Contains("Image studio", trail.TextContent, StringComparison.Ordinal);
        Assert.Equal($"campaigns/{CampaignId}/focus?artifact={ArtifactId}", trail.QuerySelector("a")!.GetAttribute("href"));
    }

    private IRenderedComponent<CampaignShell> RenderShell(CampaignView view, string relativeUrl)
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(relativeUrl);
        return Render<CampaignShell>(p => p
            .Add(c => c.CampaignId, CampaignId)
            .Add(c => c.View, view)
            .Add(c => c.ChildContent, (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>view body</p>"))));
    }

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Header campaign", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, BrandId: BrandId);
}
