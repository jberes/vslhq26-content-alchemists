using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;
using Microsoft.AspNetCore.Components.Forms;
using System.Net;
using System.Reflection;

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

    [Fact]
    public async Task An_empty_brand_can_open_the_dialog_upload_a_screenshot_and_reuse_it_from_the_library()
    {
        var libraryAssetId = Guid.Parse("a1111111-1111-1111-1111-777777777777");
        var brandLinkId = Guid.Parse("a1111111-1111-1111-1111-888888888888");
        Http.OnGet($"api/v1/brands/{BrandId}/assets", new List<BrandAssetResponse>());
        Http.OnPost("api/v1/assets", new AssetResponse(
            libraryAssetId, "new-grid.png", "image/png", 4,
            $"assets/{libraryAssetId}/new-grid.png", DateTimeOffset.UtcNow));
        Http.OnStatus(HttpMethod.Post, $"api/v1/blob/assets/{libraryAssetId}/content", HttpStatusCode.NoContent);
        Http.OnPost($"api/v1/brands/{BrandId}/assets",
            new BrandAssetResponse(brandLinkId, BrandId, libraryAssetId, "product", "current grid screen",
                "new-grid.png", "image/png", DateTimeOffset.UtcNow));
        Http.OnPost("api/v1/blob/assets/thumbs", new List<AssetThumb>
        {
            new(libraryAssetId, "https://sas.example/new-grid-thumb.png", true),
        });

        var view = await OpenDrawerAsync();
        var choose = ChooseButton(view);
        Assert.False(choose.HasAttribute("disabled"));
        await choose.ClickAsync();

        var dialog = view.Find(".cm-kitpicker");
        var file = dialog.QuerySelector("input[aria-label='Upload a reference image']")!;
        Assert.True(file.HasAttribute("disabled"));
        dialog.QuerySelector("input[aria-label='Reference description']")!.Input("current grid screen");
        Assert.False(view.Find(".cm-kitpicker input[aria-label='Upload a reference image']").HasAttribute("disabled"));

        await InvokePrivateAsync(view, "UploadKitAssetAsync",
            new InputFileChangeEventArgs([new TestBrowserFile("new-grid.png", "image/png", [1, 2, 3, 4])]));

        await view.WaitForAssertionAsync(() =>
            Assert.Contains("current grid screen", view.Find(".cm-kitpicker").TextContent, StringComparison.Ordinal));
        Assert.Contains("attach automatically", view.Find(".cm-kitpicker__preview").TextContent, StringComparison.Ordinal);
        Assert.Contains(Http.Bodies, request => request.Method == HttpMethod.Post && request.Path == "api/v1/assets");
        Assert.Contains(Http.Bodies, request => request.Method == HttpMethod.Post
            && request.Path.EndsWith($"blob/assets/{libraryAssetId}/content", StringComparison.Ordinal));
        Assert.Contains(Http.Bodies, request => request.Method == HttpMethod.Post
            && request.Path.EndsWith($"brands/{BrandId}/assets", StringComparison.Ordinal));
    }

    // ---- helpers ---------------------------------------------------------------

    /// <summary>
    /// The kit upload's Choose File is gated on the description (it becomes prompt text).
    /// With no explanation the gate read as a broken button. The hint states the rule while
    /// the gate is closed, and the picker unlocks the moment a description is typed.
    /// </summary>
    [Fact]
    public async Task Choose_file_says_why_it_is_locked_and_unlocks_once_a_description_is_typed()
    {
        var takeId = Guid.NewGuid();
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/variants",
            new List<ImageVariantResponse>
            {
                new(takeId, SlotId, "https://public.example/full.webp", "https://public.example/thumb.webp",
                    "gpt-image-2", "Candidate", null, null, 1280, 720, DateTimeOffset.UtcNow),
            });

        var view = await OpenDrawerAsync();
        await view.WaitForStateAsync(() => view.FindAll(".cm-gallery__tile").Count == 1, TimeSpan.FromSeconds(5));
        await view.Find(".cm-gallery__tile").ClickAsync();
        await view.WaitForStateAsync(() => view.FindAll(".cm-lightbox").Count == 1, TimeSpan.FromSeconds(5));

        var file = view.Find(".cm-lightbox input[type=file]");
        Assert.True(file.HasAttribute("disabled"));
        Assert.Contains("Type a description first", view.Find("#cm-kit-upload-hint").TextContent, StringComparison.Ordinal);

        view.Find(".cm-lightbox input[aria-label='Description used as prompt text']").Input("the Berlin studio wall");

        file = view.Find(".cm-lightbox input[type=file]");
        Assert.False(file.HasAttribute("disabled"));
        Assert.Contains("Ready", view.Find("#cm-kit-upload-hint").TextContent, StringComparison.Ordinal);
    }

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

    private static async Task InvokePrivateAsync(
        IRenderedComponent<ImageStudioView> view, string methodName, params object[] args)
    {
        await view.InvokeAsync(async () =>
        {
            var method = typeof(ImageStudioView).GetMethod(
                methodName, BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)method.Invoke(view.Instance, args)!;
        });
    }

    private sealed class TestBrowserFile(
        string name,
        string contentType,
        byte[] bytes) : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => bytes.LongLength;
        public string ContentType => contentType;

        public Stream OpenReadStream(
            long maxAllowedSize = 512_000,
            CancellationToken cancellationToken = default) =>
            bytes.LongLength > maxAllowedSize
                ? throw new IOException("File exceeds max size.")
                : new MemoryStream(bytes, writable: false);
    }
}
