using System.Globalization;
using System.Net;
using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign.Bench;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// The manual image editor (ADR-F72) as a producer drives it: drag from the tray (the JS
/// island reports the drop), resize and crop on the canvas (reported as ratios), style in the
/// inspector, reorder in Layers, delete, undo, save. The island itself is exercised in the
/// browser by tests/e2e/image-bench.spec.js; here its reports are invoked directly.
/// </summary>
public sealed class ImageBenchTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("b3111111-1111-1111-1111-111111111111");
    private static readonly Guid SlotId = Guid.Parse("b3111111-1111-1111-1111-222222222222");
    private static readonly Guid BrandId = Guid.Parse("b3111111-1111-1111-1111-333333333333");
    private static readonly Guid PhotoLinkId = Guid.Parse("b3111111-1111-1111-1111-444444444444");
    private static readonly Guid PhotoAssetId = Guid.Parse("b3111111-1111-1111-1111-555555555555");
    private static readonly Guid LogoLinkId = Guid.Parse("b3111111-1111-1111-1111-666666666666");
    private static readonly Guid LogoAssetId = Guid.Parse("b3111111-1111-1111-1111-777777777777");

    private const string PhotoFull = "https://private.example/portrait-full.png";
    private const string PhotoThumb = "https://private.example/portrait-thumb.webp";

    private readonly FakeConfirm _confirm = new();
    private readonly List<BrandAssetResponse> _assets =
    [
        new(PhotoLinkId, BrandId, PhotoAssetId, "face", "Speaker portrait", "portrait.png", "image/png", DateTimeOffset.UtcNow),
        new(LogoLinkId, BrandId, LogoAssetId, "logo", "Logo mark", "logo.png", "image/png", DateTimeOffset.UtcNow),
    ];

    private int _closed;
    private readonly List<ImageSlotResponse> _slotChanges = [];
    private readonly List<BrandAssetResponse> _kitAdds = [];

    public ImageBenchTests()
    {
        SignInTestUser();
        Services.AddScoped<IConfirmService>(_ => _confirm);
        Http.OnGet($"api/v1/blob/assets/{PhotoAssetId}/read-sas", new ReadSas(PhotoFull));
        Http.OnGet($"api/v1/blob/assets/{LogoAssetId}/read-sas", new ReadSas("https://private.example/logo-full.png"));
    }

    [Fact]
    public async Task Dropping_a_kit_image_adds_a_layer_drawn_from_the_full_resolution_original()
    {
        var bench = RenderBench();

        await bench.Instance.DropTile("image", PhotoLinkId.ToString(), 0.5, 0.5, 0.8);

        await bench.WaitForAssertionAsync(() =>
        {
            var layer = bench.Find("[data-layer-id]");
            Assert.Equal("image", layer.GetAttribute("data-kind"));
            // A 0.8 aspect picture is height-bound: 62% of the height, centred on the drop point.
            Assert.Equal(0.62, Ratio(layer, "data-h"), 3);
            Assert.Equal(0.5, Ratio(layer, "data-x") + (Ratio(layer, "data-w") / 2), 3);
            Assert.Equal(PhotoFull, bench.Find("img[data-layer-img]").GetAttribute("src"));
        });
        Assert.Contains("Image layer", bench.Find(".cm-bench__inspector").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Tray_tiles_have_no_click_action_only_drag()
    {
        var bench = RenderBench();

        foreach (var tile in bench.FindAll("[data-bench-tile]"))
        {
            Assert.Throws<Bunit.MissingEventHandlerException>(() => tile.Click());
        }
        Assert.Empty(bench.FindAll("[data-layer-id]"));
        Assert.Equal(6, bench.FindAll("[data-bench-tile]").Count);
    }

    [Fact]
    public async Task The_resolution_readout_says_sharp_or_upscaled_from_the_measured_original()
    {
        var bench = RenderBench();
        await bench.Instance.DropTile("image", PhotoLinkId.ToString(), 0.5, 0.5, 1.5);

        await bench.Instance.ImageMeasured(PhotoLinkId.ToString(), 3000, 2000);
        await bench.WaitForAssertionAsync(() =>
        {
            var readout = bench.Find("[data-testid=resolution]").TextContent;
            Assert.Contains("Source 3000×2000", readout, StringComparison.Ordinal);
            Assert.Contains("Sharp", readout, StringComparison.Ordinal);
        });

        await bench.Instance.DropTile("image", LogoLinkId.ToString(), 0.3, 0.3, 1);
        await bench.Instance.ImageMeasured(LogoLinkId.ToString(), 120, 120);
        await bench.WaitForAssertionAsync(() =>
            Assert.Contains("Upscaled", bench.Find("[data-testid=resolution]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Circle_and_square_frames_are_physically_square_and_save_their_shape()
    {
        var bench = RenderBench();
        await bench.Instance.DropTile("image", PhotoLinkId.ToString(), 0.5, 0.5, 1.5);
        await bench.Instance.ImageMeasured(PhotoLinkId.ToString(), 3000, 2000);

        await ClickButton(bench, "Circle");

        await bench.WaitForAssertionAsync(() =>
        {
            var layer = bench.Find("[data-layer-id]");
            Assert.Equal("circle", layer.GetAttribute("data-shape"));
            Assert.Equal(Ratio(layer, "data-w") * 1280, Ratio(layer, "data-h") * 720, 1);
            Assert.Contains("border-radius:50%", bench.Find(".cm-bench__frame").GetAttribute("style"), StringComparison.Ordinal);
        });

        var body = await SaveAndReadBodyAsync(bench);
        Assert.Contains("\"shape\":\"circle\"", body, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"image\"", body, StringComparison.Ordinal);
        Assert.Contains(PhotoLinkId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_crop_commit_from_the_canvas_saves_focus_and_zoom()
    {
        var bench = RenderBench();
        await bench.Instance.DropTile("image", PhotoLinkId.ToString(), 0.5, 0.5, 1.5);
        var id = LayerId(bench);

        await bench.Instance.Command("crop");
        await bench.WaitForAssertionAsync(() =>
            Assert.StartsWith("CROP", bench.Find(".cm-bench__mode").TextContent, StringComparison.Ordinal));
        Assert.NotNull(bench.Find("[role=toolbar][aria-label=Crop] input[aria-label='Crop zoom']"));

        await bench.Instance.CommitCrop(id, 0.2, 0.25, 0.3, 0.4, 2.25, 0.8, 0.3);
        await bench.Instance.Command("escape");
        await bench.WaitForAssertionAsync(() =>
            Assert.Equal("SELECT", bench.Find(".cm-bench__mode").TextContent));

        var body = await SaveAndReadBodyAsync(bench);
        Assert.Contains("\"zoom\":2.25", body, StringComparison.Ordinal);
        Assert.Contains("\"focusX\":0.8", body, StringComparison.Ordinal);
        Assert.Contains("\"focusY\":0.3", body, StringComparison.Ordinal);
        Assert.Contains("\"x\":0.2", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Effects_preview_as_a_drop_shadow_and_inset_border_and_save_as_numbers()
    {
        var bench = RenderBench();
        await bench.Instance.DropTile("image", PhotoLinkId.ToString(), 0.5, 0.5, 1.5);

        await ClickButton(bench, "Sticker");

        await bench.WaitForAssertionAsync(() =>
        {
            Assert.Contains("filter:drop-shadow(0cqh 1.67cqh 1.65cqh rgba(0,0,0,0.45))", bench.Find("[data-layer-id]").GetAttribute("style"), StringComparison.Ordinal);
            Assert.Contains("inset 0 0 0 1.39cqh #FFFFFF", bench.Find(".cm-bench__edges").GetAttribute("style"), StringComparison.Ordinal);
        });
        await bench.Find("input[aria-label='Border width']").ChangeAsync("20");
        await bench.Find("input[aria-label='Effect depth']").ChangeAsync("0.8");

        var body = await SaveAndReadBodyAsync(bench);
        Assert.Contains("\"preset\":\"sticker\"", body, StringComparison.Ordinal);
        Assert.Contains("\"depth\":0.8", body, StringComparison.Ordinal);
        Assert.Contains($"\"borderWidth\":{(20d / 720).ToString(CultureInfo.InvariantCulture)}", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_text_layer_takes_font_size_colour_opacity_and_background()
    {
        var bench = RenderBench();
        await bench.Instance.DropTile("text", "headline", 0.3, 0.3, 0);

        await bench.Find("select[aria-label=Font]").ChangeAsync("Barlow");
        await bench.Find("input[aria-label='Font size']").ChangeAsync("120");
        await bench.Find("input[aria-label='Font colour']").ChangeAsync("#ff0000");
        await bench.Find("input[aria-label='Text opacity']").ChangeAsync("0.5");
        await bench.Find("input[aria-label='Background colour']").ChangeAsync("#336699");
        await bench.Find("input[aria-label='Background opacity']").ChangeAsync("0.35");
        await ClickButton(bench, "Align center");

        await bench.WaitForAssertionAsync(() =>
        {
            var text = bench.Find(".cm-bench__text").GetAttribute("style")!;
            Assert.Contains("font-family:\"Barlow\"", text, StringComparison.Ordinal);
            Assert.Contains("font-size:16.6667cqh", text, StringComparison.Ordinal);
            Assert.Contains("color:rgba(255,0,0,0.5)", text, StringComparison.Ordinal);
            Assert.Contains("text-align:center", text, StringComparison.Ordinal);
            Assert.Contains("background:rgba(51,102,153,0.35)", bench.Find(".cm-bench__frame").GetAttribute("style"), StringComparison.Ordinal);
        });

        var body = await SaveAndReadBodyAsync(bench);
        Assert.Contains("\"kind\":\"text\"", body, StringComparison.Ordinal);
        Assert.Contains("\"fontFamily\":\"Barlow\"", body, StringComparison.Ordinal);
        Assert.Contains("\"color\":\"#FF0000\"", body, StringComparison.Ordinal);
        Assert.Contains("\"textOpacity\":0.5", body, StringComparison.Ordinal);
        Assert.Contains("\"color\":\"#336699\"", body, StringComparison.Ordinal);
        Assert.Contains("\"opacity\":0.35", body, StringComparison.Ordinal);
        Assert.Contains($"\"fontSize\":{(120d / 720).ToString(CultureInfo.InvariantCulture)}", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_face_without_heavier_weights_disables_them_and_clamps_the_weight()
    {
        var bench = RenderBench();
        await bench.Instance.DropTile("text", "headline", 0.3, 0.3, 0);

        await bench.Find("select[aria-label=Font]").ChangeAsync("Anton");

        await bench.WaitForAssertionAsync(() =>
        {
            var weights = bench.FindAll("[aria-label='Font weight'] button");
            Assert.False(weights.Single(b => b.TextContent == "Regular").HasAttribute("disabled"));
            Assert.All(weights.Where(b => b.TextContent != "Regular"), b => Assert.True(b.HasAttribute("disabled")));
            Assert.Equal("true", weights.Single(b => b.TextContent == "Regular").GetAttribute("aria-pressed"));
        });
    }

    [Fact]
    public async Task Delete_works_from_the_key_the_row_bin_and_the_toolbar_bin_then_saving_clears()
    {
        Http.OnAsync(HttpMethod.Delete, $"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/overlay",
            () => Task.FromResult(StubHttpHandler.Json(Filled())));
        var saved = Filled() with
        {
            Overlay = new OverlaySpec([new OverlayBox("kept", "Already saved", 0.1, 0.1, 0.3, 0.1, Kind: "text")]),
        };
        var bench = RenderBench(saved);
        await bench.Instance.DropTile("text", "subhead", 0.3, 0.6, 0);
        await bench.Instance.DropTile("image", PhotoLinkId.ToString(), 0.7, 0.5, 1.5);
        Assert.Equal(3, bench.FindAll("[data-layer-id]").Count);

        await bench.Instance.Command("delete");
        await bench.WaitForAssertionAsync(() => Assert.Equal(2, bench.FindAll("[data-layer-id]").Count));

        await bench.Find("button[aria-label='Delete A short supporting line']").ClickAsync();
        await bench.WaitForAssertionAsync(() => Assert.Single(bench.FindAll("[data-layer-id]")));

        await bench.Find(".cm-bench__row").ClickAsync();
        await bench.Find("[role=toolbar] button[aria-label='Delete layer']").ClickAsync();
        await bench.WaitForAssertionAsync(() => Assert.Empty(bench.FindAll("[data-layer-id]")));

        await bench.FindAll(".cm-bench__head button").Single(b => b.TextContent.Trim() == "Save image").ClickAsync();
        await bench.WaitForAssertionAsync(() =>
        {
            Assert.Contains(Http.Requests, r => r.Method == HttpMethod.Delete
                && r.RequestUri!.AbsolutePath.EndsWith("/overlay", StringComparison.Ordinal));
            Assert.Contains("All layers deleted", bench.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Z_order_moves_and_row_reordering_change_the_paint_order_that_is_saved()
    {
        var bench = RenderBench();
        await bench.Instance.DropTile("image", PhotoLinkId.ToString(), 0.5, 0.5, 1.5);
        var photo = LayerId(bench);
        await bench.Instance.DropTile("text", "headline", 0.3, 0.3, 0);
        await bench.Instance.DropTile("text", "label", 0.2, 0.2, 0);
        var order = PaintOrder(bench);
        var (headline, label) = (order[1], order[2]);

        await bench.Instance.SelectLayer(photo);
        await bench.Instance.Command("front");
        await bench.WaitForAssertionAsync(() => Assert.Equal([headline, label, photo], PaintOrder(bench)));

        await bench.Instance.Command("backward");
        await bench.WaitForAssertionAsync(() => Assert.Equal([headline, photo, label], PaintOrder(bench)));

        await bench.Instance.Command("back");
        await bench.WaitForAssertionAsync(() => Assert.Equal([photo, headline, label], PaintOrder(bench)));

        await bench.Instance.Command("forward");
        await bench.WaitForAssertionAsync(() => Assert.Equal([headline, photo, label], PaintOrder(bench)));

        // Layers lists the FRONT first; dragging the photo's row to the top puts it in front.
        await bench.Instance.ReorderLayer(photo, 0);
        await bench.WaitForAssertionAsync(() =>
        {
            Assert.Equal([headline, label, photo], PaintOrder(bench));
            Assert.Equal([photo, label, headline], bench.FindAll("[data-bench-row]").Select(r => r.GetAttribute("data-bench-row")!).ToList());
        });

        var body = await SaveAndReadBodyAsync(bench);
        Assert.True(body.IndexOf(headline, StringComparison.Ordinal) < body.IndexOf(label, StringComparison.Ordinal));
        Assert.True(body.IndexOf(label, StringComparison.Ordinal) < body.IndexOf(photo, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Undo_and_redo_walk_the_history_and_a_run_of_nudges_is_one_step()
    {
        var bench = RenderBench();
        await bench.Instance.DropTile("text", "headline", 0.5, 0.5, 0);
        var id = LayerId(bench);
        var x = Ratio(bench.Find("[data-layer-id]"), "data-x");

        await bench.Instance.Nudge(id, 1d / 1280, 0);
        await bench.Instance.Nudge(id, 1d / 1280, 0);
        await bench.Instance.Nudge(id, 1d / 1280, 0);
        await bench.WaitForAssertionAsync(() => Assert.Equal(x + (3d / 1280), Ratio(bench.Find("[data-layer-id]"), "data-x"), 5));

        await bench.Instance.Command("undo");
        await bench.WaitForAssertionAsync(() => Assert.Equal(x, Ratio(bench.Find("[data-layer-id]"), "data-x"), 5));

        await bench.Instance.Command("undo");
        await bench.WaitForAssertionAsync(() => Assert.Empty(bench.FindAll("[data-layer-id]")));

        await bench.Instance.Command("redo");
        await bench.WaitForAssertionAsync(() => Assert.Single(bench.FindAll("[data-layer-id]")));
    }

    [Fact]
    public async Task Escape_leaves_crop_then_clears_selection_then_closes_asking_first_when_unsaved()
    {
        var bench = RenderBench();
        await bench.Instance.DropTile("image", PhotoLinkId.ToString(), 0.5, 0.5, 1.5);
        await bench.Instance.Command("crop");

        await bench.Instance.Command("escape");
        await bench.WaitForAssertionAsync(() => Assert.Equal("SELECT", bench.Find(".cm-bench__mode").TextContent));
        Assert.NotEmpty(bench.FindAll(".cm-bench__row--on"));

        await bench.Instance.Command("escape");
        await bench.WaitForAssertionAsync(() => Assert.Empty(bench.FindAll(".cm-bench__row--on")));

        _confirm.Answer = false;
        await bench.Instance.Command("escape");
        Assert.Single(_confirm.Requests);
        Assert.Equal(0, _closed);

        _confirm.Answer = true;
        await bench.Instance.Command("escape");
        Assert.Equal(1, _closed);
    }

    [Fact]
    public async Task Locked_and_hidden_layers_render_that_way_and_save_their_flags()
    {
        var bench = RenderBench();
        await bench.Instance.DropTile("text", "headline", 0.3, 0.3, 0);
        await bench.Instance.DropTile("text", "subhead", 0.3, 0.6, 0);

        await bench.Find("button[aria-label='Lock YOUR HEADLINE']").ClickAsync();
        await bench.Find("button[aria-label='Hide A short supporting line']").ClickAsync();

        await bench.WaitForAssertionAsync(() =>
        {
            var layers = bench.FindAll("[data-layer-id]");
            Assert.Equal("true", layers[0].GetAttribute("data-locked"));
            Assert.Equal("false", layers[1].GetAttribute("data-visible"));
            Assert.NotNull(bench.Find("button[aria-label='Unlock YOUR HEADLINE']"));
            Assert.NotNull(bench.Find("button[aria-label='Show A short supporting line']"));
        });

        var body = await SaveAndReadBodyAsync(bench);
        Assert.Contains("\"locked\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"visible\":false", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Typing_on_the_canvas_edits_the_text_and_renames_the_layer()
    {
        var bench = RenderBench();
        await bench.Instance.DropTile("text", "subhead", 0.3, 0.3, 0);

        await bench.Instance.Command("activate");
        await bench.WaitForAssertionAsync(() => Assert.NotNull(bench.Find("textarea[data-bench-textedit]")));
        await bench.Find("textarea[data-bench-textedit]").InputAsync("Live demo today");
        await bench.Instance.Command("escape");

        await bench.WaitForAssertionAsync(() =>
        {
            Assert.Empty(bench.FindAll("textarea[data-bench-textedit]"));
            Assert.Equal("Live demo today", bench.Find("[data-words]").TextContent);
            Assert.NotNull(bench.Find("button[aria-label='Delete Live demo today']"));
        });
    }

    [Fact]
    public async Task Text_that_needs_more_room_grows_its_box_without_adding_an_undo_step()
    {
        var bench = RenderBench();
        await bench.Instance.DropTile("text", "headline", 0.5, 0.5, 0);
        var id = LayerId(bench);

        await bench.Instance.TextNeedsHeight(id, 0.55);
        await bench.WaitForAssertionAsync(() => Assert.Equal(0.55, Ratio(bench.Find("[data-layer-id]"), "data-h"), 4));

        await bench.Instance.Command("undo");
        await bench.WaitForAssertionAsync(() => Assert.Empty(bench.FindAll("[data-layer-id]")));
    }

    [Fact]
    public async Task Legacy_boxes_open_as_layers_and_save_back_as_layers()
    {
        var legacy = Filled() with
        {
            Overlay = new OverlaySpec([new OverlayBox("old-1", "Deploy time, halved", 0.08, 0.72, 0.84, 0.18, 0.11, 700, "#F2F2F3", "left", new OverlayBand("#000000", 0.9))]),
        };
        var bench = RenderBench(legacy);

        var layer = bench.Find("[data-layer-id=old-1]");
        Assert.Equal("text", layer.GetAttribute("data-kind"));
        Assert.Equal("Saved", SaveButton(bench).TextContent.Trim());

        await bench.Instance.Nudge("old-1", 0, 4d / 720);
        var body = await SaveAndReadBodyAsync(bench);
        Assert.Contains("\"kind\":\"text\"", body, StringComparison.Ordinal);
        Assert.Contains("Deploy time, halved", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_legacy_uncropped_picture_keeps_its_whole_image_once_measured()
    {
        var legacy = Filled() with
        {
            Overlay = new OverlaySpec([new OverlayBox("old-pic", string.Empty, 0.25, 0.25, 0.5, 0.5, LogoAssetId: PhotoLinkId)]),
        };
        var bench = RenderBench(legacy);

        await bench.Instance.ImageMeasured(PhotoLinkId.ToString(), 2000, 1000);

        await bench.WaitForAssertionAsync(() =>
        {
            var layer = bench.Find("[data-layer-id=old-pic]");
            // 640×360 px frame, 2:1 picture → contained at 640×320, centred vertically.
            Assert.Equal(0.5, Ratio(layer, "data-w"), 4);
            Assert.Equal(320d / 720, Ratio(layer, "data-h"), 4);
            Assert.Equal("Saved", SaveButton(bench).TextContent.Trim());
        });
    }

    [Fact]
    public async Task On_an_empty_canvas_the_first_picture_becomes_the_background()
    {
        var empty = Filled() with { BaseImageUrl = null, PublishedUrl = null, State = "Empty" };
        Http.OnPost($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/base", new OverlaySaveResult(Filled() with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1) }, null));
        var bench = RenderBench(empty);
        Assert.Contains("Start with a background.", bench.Markup, StringComparison.Ordinal);

        await bench.Instance.DropTile("image", PhotoLinkId.ToString(), 0.5, 0.5, 1.5);

        await bench.WaitForAssertionAsync(() =>
        {
            var request = Http.Bodies.Single(b => b.Method == HttpMethod.Post && b.Path.EndsWith("/base", StringComparison.Ordinal));
            Assert.Contains(PhotoLinkId.ToString(), request.Body, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(bench.Find("img.cm-bench__bg"));
            Assert.Empty(bench.FindAll("[data-layer-id]"));
            Assert.Single(_slotChanges);
        });
    }

    [Fact]
    public async Task A_file_dropped_on_the_background_row_replaces_the_backdrop()
    {
        Http.OnPost($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/base", new OverlaySaveResult(Filled() with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1) }, null));
        var bench = RenderBench();

        await bench.Instance.DropFile("backdrop.png", "image/png", [1, 2, 3, 4], 0.5, 0.5, background: true);

        await bench.WaitForAssertionAsync(() =>
        {
            var request = Http.Bodies.Single(b => b.Method == HttpMethod.Post && b.Path.EndsWith("/base", StringComparison.Ordinal));
            Assert.Contains("\"imageBase64\":\"AQIDBA==\"", request.Body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_file_dropped_on_the_canvas_joins_the_kit_then_becomes_a_layer()
    {
        var newAsset = Guid.NewGuid();
        var newLink = Guid.NewGuid();
        Http.OnPost("api/v1/assets", new AssetResponse(newAsset, "hero.png", "image/png", 4, "assets/hero.png", DateTimeOffset.UtcNow));
        Http.OnStatus(HttpMethod.Post, $"api/v1/blob/assets/{newAsset}/content", HttpStatusCode.NoContent);
        Http.OnPost($"api/v1/brands/{BrandId}/assets", new BrandAssetResponse(newLink, BrandId, newAsset, "other", "hero", "hero.png", "image/png", DateTimeOffset.UtcNow));
        Http.OnPost("api/v1/blob/assets/thumbs", new List<AssetThumb> { new(newAsset, "https://private.example/hero-thumb.webp", true) });
        Http.OnGet($"api/v1/blob/assets/{newAsset}/read-sas", new ReadSas("https://private.example/hero-full.png"));
        var bench = RenderBench();

        await bench.Instance.DropFile("hero.png", "image/png", [1, 2, 3, 4], 0.4, 0.4, background: false);

        await bench.WaitForAssertionAsync(() =>
        {
            var link = Http.Bodies.Single(b => b.Method == HttpMethod.Post && b.Path.EndsWith($"brands/{BrandId}/assets", StringComparison.Ordinal));
            Assert.Contains("\"kind\":\"other\"", link.Body, StringComparison.Ordinal);
            Assert.Single(_kitAdds);
            Assert.Equal("https://private.example/hero-full.png", bench.Find("img[data-layer-img]").GetAttribute("src"));
            Assert.Contains(bench.FindAll("[data-bench-tile]"), t => t.GetAttribute("data-tile-key") == newLink.ToString());
        });
    }

    [Fact]
    public async Task Duplicate_offsets_a_copy_and_tab_cycles_from_the_front()
    {
        var bench = RenderBench();
        await bench.Instance.DropTile("text", "headline", 0.4, 0.4, 0);
        var original = LayerId(bench);

        await bench.Instance.Command("duplicate");
        await bench.WaitForAssertionAsync(() => Assert.Equal(2, bench.FindAll("[data-layer-id]").Count));
        var copy = PaintOrder(bench)[1];
        Assert.NotEqual(original, copy);
        Assert.NotNull(bench.Find("button[aria-label='Delete YOUR HEADLINE copy']"));

        await bench.Instance.SelectLayer(null);
        await bench.Instance.Command("next");
        await bench.WaitForAssertionAsync(() => Assert.Equal(copy, bench.Find(".cm-bench__row--on").GetAttribute("data-bench-row")));
        await bench.Instance.Command("next");
        await bench.WaitForAssertionAsync(() => Assert.Equal(original, bench.Find(".cm-bench__row--on").GetAttribute("data-bench-row")));
    }

    [Fact]
    public async Task An_image_holds_at_most_the_spec_limit_of_layers()
    {
        var bench = RenderBench();
        for (var i = 0; i < OverlaySpec.MaxBoxes + 2; i++)
        {
            await bench.Instance.DropTile("text", "label", 0.1 + (i * 0.01), 0.5, 0);
        }

        await bench.WaitForAssertionAsync(() => Assert.Equal(OverlaySpec.MaxBoxes, bench.FindAll("[data-layer-id]").Count));
    }

    [Fact]
    public async Task A_save_the_server_refuses_says_why_and_stays_unsaved()
    {
        Http.OnStatus(HttpMethod.Put, $"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/overlay", HttpStatusCode.BadRequest);
        var bench = RenderBench();
        await bench.Instance.DropTile("text", "headline", 0.4, 0.4, 0);

        await SaveButton(bench).ClickAsync();

        await bench.WaitForAssertionAsync(() =>
        {
            Assert.Contains("Not saved", bench.Find(".cm-bench__status-text").TextContent, StringComparison.Ordinal);
            Assert.Equal("Save image", SaveButton(bench).TextContent.Trim());
        });
    }

    // ---- helpers ---------------------------------------------------------------------

    private IRenderedComponent<ImageBench> RenderBench(ImageSlotResponse? slot = null) =>
        Render<ImageBench>(p => p
            .Add(b => b.CampaignId, CampaignId)
            .Add(b => b.Slot, slot ?? Filled())
            .Add(b => b.BrandId, BrandId)
            .Add(b => b.BrandAssets, _assets)
            .Add(b => b.KitPreviews, new Dictionary<Guid, string>
            {
                [PhotoAssetId] = PhotoThumb,
                [LogoAssetId] = "https://private.example/logo-thumb.webp",
            })
            .Add(b => b.OnClose, () => _closed++)
            .Add(b => b.OnSlotChanged, s => _slotChanges.Add(s))
            .Add(b => b.OnKitAssetAdded, added => _kitAdds.Add(added.Asset)));

    private async Task<string> SaveAndReadBodyAsync(IRenderedComponent<ImageBench> bench)
    {
        Http.OnPut($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/overlay", new OverlaySaveResult(Filled(), false));
        await SaveButton(bench).ClickAsync();
        string body = string.Empty;
        await bench.WaitForAssertionAsync(() =>
        {
            body = Http.Bodies.Last(b => b.Method == HttpMethod.Put && b.Path.EndsWith("/overlay", StringComparison.Ordinal)).Body;
            Assert.Contains("Saved and composited", bench.Markup, StringComparison.Ordinal);
        });
        return body;
    }

    private static AngleSharp.Dom.IElement SaveButton(IRenderedComponent<ImageBench> bench) =>
        bench.FindAll(".cm-bench__head button").Single(b => b.TextContent.Trim() is "Save image" or "Saved" or "Saving…");

    private static async Task ClickButton(IRenderedComponent<ImageBench> bench, string name) =>
        await bench.FindAll("button").First(b => b.TextContent.Trim() == name || b.GetAttribute("aria-label") == name).ClickAsync();

    private static string LayerId(IRenderedComponent<ImageBench> bench) =>
        bench.FindAll("[data-layer-id]")[^1].GetAttribute("data-layer-id")!;

    private static List<string> PaintOrder(IRenderedComponent<ImageBench> bench) =>
        [.. bench.FindAll("[data-layer-id]").Select(l => l.GetAttribute("data-layer-id")!)];

    private static double Ratio(AngleSharp.Dom.IElement element, string attribute) =>
        double.Parse(element.GetAttribute(attribute)!, CultureInfo.InvariantCulture);

    private static ImageSlotResponse Filled() =>
        new(SlotId, CampaignId, "youtube-thumbnail", 1280, 720, null, null, null, null, true,
            "Filled", "https://public.example/base.webp", "https://public.example/base.webp", DateTimeOffset.UtcNow);

    private sealed class FakeConfirm : IConfirmService
    {
        public bool Answer { get; set; } = true;

        public List<ConfirmRequest> Requests { get; } = [];

        public Task<bool> ConfirmAsync(ConfirmRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(Answer);
        }
    }
}
