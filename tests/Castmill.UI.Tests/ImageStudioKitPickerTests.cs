using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// The reference-image picker dialog. The drawer used to inline every face and background in
/// the brand kit, which made the editor mostly other people's assets; now it shows only the
/// current selections as removable chips, and the kit opens in a master–detail dialog —
/// grouped list on the left, judgeable preview on the right, one selection per type.
/// </summary>
public sealed class ImageStudioKitPickerTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("a1111111-1111-1111-1111-111111111111");
    private static readonly Guid SlotId = Guid.Parse("a1111111-1111-1111-1111-222222222222");
    private static readonly Guid BrandId = Guid.Parse("a1111111-1111-1111-1111-333333333333");
    private static readonly Guid HostFaceId = Guid.Parse("a1111111-1111-1111-1111-444444444444");
    private static readonly Guid GuestFaceId = Guid.Parse("a1111111-1111-1111-1111-555555555555");
    private static readonly Guid WallId = Guid.Parse("a1111111-1111-1111-1111-666666666666");

    public ImageStudioKitPickerTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet("api/v1/ai/status", new Castmill.Core.Ai.AiStatusResponse(
            "config", true, new Dictionary<string, string>(), false, null,
            [new Castmill.Core.Ai.ImageProviderReadiness("foundry", true, null)]));

        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [], [Slot()], 0, 1,
                new BrandSummaryResponse(BrandId, "Ignite UI")));

        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/variants",
            new List<ImageVariantResponse>());

        Http.OnGet($"api/v1/brands/{BrandId}/assets", new List<BrandAssetResponse>
        {
            Asset(HostFaceId, "face", "the host, short dark hair"),
            Asset(GuestFaceId, "face", "the guest"),
            Asset(WallId, "background", "the Berlin studio wall"),
        });
        foreach (var assetId in new[] { HostFaceId, GuestFaceId, WallId })
        {
            Http.OnGet($"api/v1/blob/assets/{AssetBlobId(assetId)}/read-sas",
                new ReadSas($"https://sas.example/{assetId}.png"));
        }

        Http.OnPatch($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}", Slot());
    }

    [Fact]
    public async Task The_drawer_shows_a_choose_button_not_the_whole_kit()
    {
        var view = await OpenDrawerAsync();

        // No inline asset gallery — the kit stays behind the button until asked for.
        Assert.Empty(view.FindAll(".cm-kitpicker"));
        Assert.Empty(view.FindAll(".cm-studio__picks"));
        Assert.Contains("Choose references…", ChooseButton(view).TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_dialog_lists_the_kit_by_type_and_previews_the_clicked_item()
    {
        var view = await OpenDrawerAsync();
        await ChooseButton(view).ClickAsync();

        var dialog = view.Find(".cm-kitpicker");
        Assert.Contains("Face", dialog.TextContent, StringComparison.Ordinal);
        Assert.Contains("Background", dialog.TextContent, StringComparison.Ordinal);

        var items = view.FindAll(".cm-kitpicker__item");
        Assert.Equal(3, items.Count);

        var guest = items.First(i => i.TextContent.Contains("the guest", StringComparison.Ordinal));
        await guest.ClickAsync();
        Assert.Contains("the guest", view.Find(".cm-kitpicker__preview").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selecting_in_the_preview_patches_the_slot_and_shows_a_removable_chip()
    {
        var view = await OpenDrawerAsync();
        await ChooseButton(view).ClickAsync();

        var wall = view.FindAll(".cm-kitpicker__item")
            .First(i => i.TextContent.Contains("Berlin studio wall", StringComparison.Ordinal));
        await wall.ClickAsync();
        await view.FindAll(".cm-kitpicker__preview button")
            .First(b => b.TextContent.Contains("Use as the background reference", StringComparison.Ordinal))
            .ClickAsync();

        var body = Http.Bodies.Last(b =>
            b.Method == HttpMethod.Patch
            && b.Path.EndsWith($"image-slots/{SlotId}", StringComparison.Ordinal)).Body;
        Assert.Contains(WallId.ToString(), body, StringComparison.OrdinalIgnoreCase);

        await view.Find(".cm-kitpicker__actions button").ClickAsync(); // Done
        Assert.Empty(view.FindAll(".cm-kitpicker"));
        var chip = view.Find(".cm-studio__refchip");
        Assert.Contains("Background · the Berlin studio wall", chip.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Escape_closes_the_dialog_but_leaves_the_drawer_open()
    {
        var view = await OpenDrawerAsync();
        await ChooseButton(view).ClickAsync();

        await view.Find(".cm-kitpicker").KeyDownAsync(
            new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(view.FindAll(".cm-kitpicker"));
        Assert.NotEmpty(view.FindAll(".cm-studio__drawer"));
    }

    // ---- helpers ---------------------------------------------------------------

    private async Task<IRenderedComponent<ImageStudioView>> OpenDrawerAsync()
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__card:not(.cm-studio__card--add)").Count > 0,
            TimeSpan.FromSeconds(5));
        await view.Find(".cm-studio__card:not(.cm-studio__card--add)").ClickAsync();
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__refbar button").Count > 0, TimeSpan.FromSeconds(5));
        return view;
    }

    private static AngleSharp.Dom.IElement ChooseButton(IRenderedComponent<ImageStudioView> view) =>
        view.FindAll(".cm-studio__refbar button")
            .First(b => b.TextContent.Contains("references…", StringComparison.Ordinal)
                     || b.TextContent.Contains("Change…", StringComparison.Ordinal));

    private static ImageSlotResponse Slot() => new(
        SlotId, CampaignId, "youtube-thumbnail", 1280, 720,
        "a bold thumbnail", "gpt-image-2", null, null, true,
        "Empty", null, null, DateTimeOffset.UtcNow);

    /// <summary>The library-asset id behind a brand link — deterministic so SAS stubs line up.</summary>
    private static Guid AssetBlobId(Guid linkId) =>
        new(linkId.ToString()[..24] + "999999999999");

    private static BrandAssetResponse Asset(Guid id, string kind, string label) =>
        new(id, BrandId, AssetBlobId(id), kind, label, $"{kind}.png", "image/png", DateTimeOffset.UtcNow);

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);
}
