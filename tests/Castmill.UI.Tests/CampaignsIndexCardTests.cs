using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages;

namespace Castmill.UI.Tests;

/// <summary>
/// Campaign cards carry the campaign's most recently placed image in the media band —
/// reference only, cover-cropped — and keep the duotone placeholder when nothing has been
/// placed yet, so an image-less campaign never shows a broken band.
/// </summary>
public sealed class CampaignsIndexCardTests : CastmillUiTestContext
{
    private static readonly Guid WithImage = Guid.Parse("b1111111-1111-1111-1111-111111111111");
    private static readonly Guid WithoutImage = Guid.Parse("b1111111-1111-1111-1111-222222222222");

    public CampaignsIndexCardTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse>
        {
            Campaign(WithImage, "Webinar campaign"),
            Campaign(WithoutImage, "Podcast campaign"),
        });
        Http.OnGet("api/v1/campaigns/dashboard", new DashboardResponse(
            [], [],
            [
                new CampaignCounts(WithImage, 5, 0, 2, 6,
                    "https://public.example/campaigns/x/images/blog-header.webp?v=1754761600"),
                new CampaignCounts(WithoutImage, 3, 0, 0, 6),
            ],
            EmptySlots: 10,
            CampaignsWithEmptySlots: 2,
            EmptySlotModels: ["gpt-image-2"],
            FirstEmptySlotCampaign: WithoutImage));
    }

    [Fact]
    public async Task A_campaign_with_a_placed_image_shows_it_in_the_card_band()
    {
        var view = Render<CampaignsIndex>();
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-campaign-card__band--image").Count == 1, TimeSpan.FromSeconds(5));

        var band = view.Find(".cm-campaign-card__band--image");
        Assert.Contains("blog-header.webp", band.GetAttribute("src"), StringComparison.Ordinal);
        Assert.Equal("lazy", band.GetAttribute("loading"));
    }

    [Fact]
    public async Task A_campaign_without_a_placed_image_keeps_the_duotone_placeholder()
    {
        var view = Render<CampaignsIndex>();
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-campaign-card").Count == 2, TimeSpan.FromSeconds(5));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-campaign-card__band--image").Count == 1, TimeSpan.FromSeconds(5));

        Assert.Single(view.FindAll(".cm-duotone"));
    }

    // ---- helpers ---------------------------------------------------------------

    private static CampaignResponse Campaign(Guid id, string name) =>
        new(id, Guid.NewGuid(), name, null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);
}
