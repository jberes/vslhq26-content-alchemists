using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// The studio's take management (items 4/11): the gallery lists persisted variants,
/// a tile opens the dialog, keep/discard flips state through the API, steering starts
/// a new run, placing goes by variant id.
/// </summary>
public sealed class ImageStudioGalleryTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("71111111-1111-1111-1111-111111111111");
    private static readonly Guid SlotId = Guid.Parse("71111111-1111-1111-1111-222222222222");
    private static readonly Guid TakeId = Guid.Parse("71111111-1111-1111-1111-333333333333");
    private static readonly Guid BlogId = Guid.Parse("71111111-1111-1111-1111-444444444444");

    public ImageStudioGalleryTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });

        var slot = new ImageSlotResponse(
            SlotId, CampaignId, "youtube-thumbnail", 1280, 720,
            "a bold thumbnail", "gpt-image-2", null, null, true,
            "Empty", null, null, DateTimeOffset.UtcNow);

        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [], [slot], 0, 6));

        Http.OnGet("api/v1/ai/status", new Castmill.Core.Ai.AiStatusResponse(
            "config", true, new Dictionary<string, string>(), false, null,
            [new Castmill.Core.Ai.ImageProviderReadiness("foundry", true, null)]));

        StubGallery(Take(TakeId, "Candidate"));
    }

    [Fact]
    public async Task The_gallery_lists_persisted_takes_with_thumbnails()
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await OpenFirstSlotAsync(view);
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-gallery__tile").Count == 1, TimeSpan.FromSeconds(5));

        var img = view.Find(".cm-gallery__tile img");
        Assert.Contains("thumbs", img.GetAttribute("src"), StringComparison.Ordinal);
        Assert.Equal("lazy", img.GetAttribute("loading"));
        Assert.DoesNotContain(view.FindAll("button"), button =>
            button.TextContent.Contains("discarded takes", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Http.Requests, request =>
            request.Method == HttpMethod.Get
            && request.RequestUri!.AbsolutePath.EndsWith($"image-slots/{SlotId}/variants", StringComparison.Ordinal)
            && request.RequestUri.Query.Contains("includeDiscarded=true", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Generate_all_pending_shows_cost_real_variant_progress_and_final_counts()
    {
        var runId = Guid.Parse("71111111-1111-1111-1111-555555555555");
        var started = DateTimeOffset.UtcNow;
        var progress = new RunProgress(
            runId, CampaignId, "Running", 1, 1,
            [new RunItem("youtube-thumbnail", true, null, null, null, 2100,
                SlotId, 1, "Succeeded")],
            started, started.AddSeconds(2));
        var gate = Http.Gate(HttpMethod.Post,
            $"api/v1/campaigns/{CampaignId}/image-slots/generate-pending");

        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForAssertionAsync(() =>
        {
            var batch = view.Find(".cm-studio__batch");
            Assert.Contains("1 eligible", batch.TextContent, StringComparison.Ordinal);
            Assert.Contains("$0.07", batch.TextContent, StringComparison.Ordinal);
        });

        Http.OnGet($"api/v1/ai/campaigns/{CampaignId}/runs/latest", progress);
        Http.OnGet($"api/v1/ai/runs/{runId}", progress with { Status = "Completed" });
        await view.FindAll("button").Single(button =>
            button.TextContent.Contains("Generate all pending", StringComparison.Ordinal)).ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var batch = view.Find(".cm-studio__batch");
            Assert.NotNull(batch.QuerySelector("progress"));
            Assert.Empty(batch.QuerySelectorAll(".cm-spinner"));
            Assert.Contains("YouTube thumbnail · take 1", batch.TextContent, StringComparison.Ordinal);
            Assert.Contains("Succeeded", batch.TextContent, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(5));
        Assert.Contains(Http.Bodies, body => body.Method == HttpMethod.Post
            && body.Path.EndsWith("image-slots/generate-pending", StringComparison.Ordinal)
            && body.Body.Contains("\"variantsPerSlot\":1", StringComparison.Ordinal));

        gate.SetResult(StubHttpHandler.Json(new ImageBatchResponse(
            runId, 1, 1, 0, 0, 1, 0,
            [new ImageBatchSlotResult(SlotId, "youtube-thumbnail", "Succeeded", 1, 1, 0)])));

        await view.WaitForAssertionAsync(() =>
        {
            var summary = view.Find(".cm-studio__batch-summary").TextContent;
            Assert.Contains("1 succeeded", summary, StringComparison.Ordinal);
            Assert.Contains("0 failed", summary, StringComparison.Ordinal);
            Assert.Contains("0 skipped", summary, StringComparison.Ordinal);
            Assert.Contains("1 takes created", summary, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Reload_reattaches_to_the_running_image_batch_and_reenables_controls_on_completion()
    {
        var runId = Guid.Parse("71111111-1111-1111-1111-666666666666");
        var started = DateTimeOffset.UtcNow;
        var running = new RunProgress(runId, CampaignId, "Running", 1, 0, [], started, started);
        Http.OnGet($"api/v1/ai/campaigns/{CampaignId}/runs/latest", running);

        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        var studioRun = Services.GetRequiredService<Castmill.UI.State.StudioRunService>();
        await view.WaitForAssertionAsync(() =>
        {
            Assert.True(studioRun.IsBatchRunning);
            Assert.Contains("Generating pending images", view.Find(".cm-studio__batch").TextContent,
                StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(5));

        Http.OnGet($"api/v1/ai/campaigns/{CampaignId}/runs/latest",
            running with { Status = "Completed", Completed = 1, UpdatedAt = started.AddSeconds(4) });

        await view.WaitForAssertionAsync(() =>
        {
            Assert.False(studioRun.IsBatchRunning);
            Assert.Contains("Pass complete", view.Find(".cm-studio__batch").TextContent,
                StringComparison.Ordinal);
            Assert.Contains(view.FindAll("button"), button =>
                button.TextContent.Contains("Generate all pending", StringComparison.Ordinal)
                && !button.HasAttribute("disabled"));
        }, TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(Http.Requests, request => request.Method == HttpMethod.Post
            && request.RequestUri!.AbsolutePath.EndsWith("generate-pending", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Locking_a_take_updates_the_take_card_immediately()
    {
        Http.OnPut($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/variants/{TakeId}/lock",
            Take(TakeId, "Candidate") with
            {
                IsLocked = true,
                CanUnlock = true,
                LockedAt = DateTimeOffset.UtcNow,
            });
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await OpenFirstSlotAsync(view);
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-gallery__tile").Count == 1, TimeSpan.FromSeconds(5));

        Assert.Empty(view.FindAll(".cm-gallery__lock"));
        await view.Find(".cm-gallery__tile").ClickAsync();
        await view.Find("button[aria-label='Lock image']").ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            Assert.Contains("cm-gallery__tile--locked", view.Find(".cm-gallery__tile").ClassList);
            Assert.Contains("Locked", view.Find(".cm-gallery__lock").TextContent,
                StringComparison.Ordinal);
            Assert.NotNull(view.Find("button[aria-label='Unlock image']"));
        });
    }

    [Fact]
    public async Task Model_is_compact_and_a_dialog_can_override_or_restore_the_workspace_default()
    {
        var slot = new ImageSlotResponse(
            SlotId, CampaignId, "youtube-thumbnail", 1280, 720,
            "a bold thumbnail", null, null, null, true,
            "Empty", null, null, DateTimeOffset.UtcNow);
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [], [slot], 0, 6));
        Http.OnGet("api/v1/settings", new List<SettingRow>
        {
            new(SettingsClient.DefaultImageModelKey, "image"),
        });
        Http.OnGet("api/v1/ai/status", new Castmill.Core.Ai.AiStatusResponse(
            "config", true,
            new Dictionary<string, string>
            {
                ["image"] = "foundry-resource:gpt-image-2",
                ["image-alt"] = "foundry-resource:mai-image-2.5-pro",
            },
            false, null,
            [new Castmill.Core.Ai.ImageProviderReadiness(
                "foundry", true, null, SupportsReferenceImages: true)]));
        Http.OnPatch($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}",
            slot with { ModelAlias = "image-alt" });

        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await OpenFirstSlotAsync(view);
        await view.WaitForAssertionAsync(() =>
        {
            Assert.Contains("gpt-image-2", view.Find(".cm-studio__models").TextContent,
                StringComparison.Ordinal);
            Assert.Contains("DEFAULT", view.Find(".cm-studio__models").TextContent,
                StringComparison.Ordinal);
            Assert.Empty(view.Find(".cm-studio__models").QuerySelectorAll("input[type='radio']"));
        });

        await view.Find(".cm-studio__models button").ClickAsync();
        var choices = view.FindAll(".cm-modelpicker__choice");
        Assert.Equal(3, choices.Count); // workspace default + two per-image choices
        var alternate = choices.Single(choice =>
            choice.TextContent.Contains("mai-image-2.5-pro", StringComparison.Ordinal));
        await alternate.QuerySelector("input")!.ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs
        {
            Value = true,
        });
        await view.FindAll(".cm-modelpicker button")
            .Single(button => button.TextContent.Contains("Use model", StringComparison.Ordinal))
            .ClickAsync();
        Assert.Contains("THIS IMAGE", view.Find(".cm-studio__models").TextContent,
            StringComparison.Ordinal);
        Assert.Contains(Http.Bodies, body => body.Method == HttpMethod.Patch
            && body.Body.Contains("\"modelAlias\":\"image-alt\"", StringComparison.Ordinal));

        Http.OnPatch($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}",
            slot with { ModelAlias = null });
        await view.Find(".cm-studio__models button").ClickAsync();
        await view.Find(".cm-modelpicker__choice input").ChangeAsync(
            new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        await view.FindAll(".cm-modelpicker button")
            .Single(button => button.TextContent.Contains("Use model", StringComparison.Ordinal))
            .ClickAsync();
        Assert.Contains("DEFAULT", view.Find(".cm-studio__models").TextContent,
            StringComparison.Ordinal);
        Assert.Contains(Http.Bodies, body => body.Method == HttpMethod.Patch
            && body.Body.Contains("\"useDefaultModel\":true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clicking_a_tile_opens_the_dialog_with_the_full_size_image_and_escape_closes_it()
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await OpenFirstSlotAsync(view);
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-gallery__tile").Count == 1, TimeSpan.FromSeconds(5));

        await view.Find(".cm-gallery__tile").ClickAsync();

        var dialog = view.Find(".cm-lightbox");
        Assert.Contains("full-size", dialog.QuerySelector(".cm-lightbox__image")!.GetAttribute("alt"),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Steer a new take", dialog.TextContent, StringComparison.Ordinal);

        await dialog.KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(view.FindAll(".cm-lightbox"));
    }

    [Fact]
    public async Task Discarding_a_take_patches_state_and_removes_it_from_the_gallery()
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await OpenFirstSlotAsync(view);
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-gallery__tile").Count == 1, TimeSpan.FromSeconds(5));

        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/variants",
            new List<ImageVariantResponse>());
        // The PATCH answers with the updated take.
        StubPatchResult(Take(TakeId, "Discarded"));

        await view.Find(".cm-gallery__tile").ClickAsync();
        var discard = view.FindAll(".cm-lightbox button")
            .First(b => b.TextContent.Trim().StartsWith("Discard", StringComparison.Ordinal));
        await discard.ClickAsync();

        Assert.Contains(Http.Requests, r =>
            r.Method == HttpMethod.Patch
            && r.RequestUri!.AbsolutePath.EndsWith($"variants/{TakeId}", StringComparison.Ordinal));
        Assert.Empty(view.FindAll(".cm-lightbox")); // dialog closed with the discard
        Assert.Empty(view.FindAll(".cm-gallery__tile"));

        var showDiscarded = view.FindAll("button").Single(button =>
            button.TextContent.Contains("Show discarded takes", StringComparison.Ordinal));
        await showDiscarded.ClickAsync();
        Assert.Single(view.FindAll(".cm-gallery__tile"));
        Assert.Contains("Hide discarded takes", view.FindAll("button")
            .Single(button => button.TextContent.Contains("discarded takes", StringComparison.OrdinalIgnoreCase))
            .TextContent, StringComparison.Ordinal);

        StubPatchResult(Take(TakeId, "Candidate"));
        await view.Find(".cm-gallery__tile").ClickAsync();
        await view.FindAll(".cm-lightbox button")
            .Single(button => button.TextContent.Contains("Restore", StringComparison.Ordinal))
            .ClickAsync();
        Assert.DoesNotContain(view.FindAll("button"), button =>
            button.TextContent.Contains("discarded takes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Placing_goes_by_variant_id_not_url()
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await OpenFirstSlotAsync(view);
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-gallery__tile").Count == 1, TimeSpan.FromSeconds(5));

        Http.OnPost($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/place",
            new PlaceResult(new ImageSlotResponse(
                SlotId, CampaignId, "youtube-thumbnail", 1280, 720,
                "a bold thumbnail", "gpt-image-2", null, null, true,
                "Filled", "https://public.example/x.webp", "https://public.example/x.webp",
                DateTimeOffset.UtcNow), null, null));

        await view.Find(".cm-gallery__tile").ClickAsync();
        var place = view.FindAll(".cm-lightbox button")
            .First(b => b.TextContent.Contains("Use this image", StringComparison.Ordinal));
        await place.ClickAsync();

        var (_, _, body) = Http.Bodies.Last(b =>
            b.Method == HttpMethod.Post && b.Path.EndsWith("/place", StringComparison.Ordinal));
        Assert.Contains(TakeId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Prompt_mode_switches_in_place_without_reloading_the_campaign()
    {
        Http.OnPatch($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}", Slot(promptMode: "Manual"));

        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await OpenFirstSlotAsync(view);
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__mode").Count == 2, TimeSpan.FromSeconds(5));
        var previewRequests = PreviewRequestCount();

        await view.FindAll(".cm-studio__mode")[1].ClickAsync();

        var modes = view.FindAll(".cm-studio__mode");
        Assert.Equal("false", modes[0].GetAttribute("aria-pressed"), ignoreCase: true);
        Assert.Equal("true", modes[1].GetAttribute("aria-pressed"), ignoreCase: true);
        Assert.Equal(previewRequests, PreviewRequestCount());
        Assert.Contains(Http.Bodies, body =>
            body.Method == HttpMethod.Patch
            && body.Path.EndsWith($"image-slots/{SlotId}", StringComparison.Ordinal)
            && body.Body.Contains("\"promptMode\":\"Manual\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Constraint_chips_are_toggleable_and_expose_their_selected_state()
    {
        Http.OnPatch($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}", Slot(prompt: "High contrast"));

        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await OpenFirstSlotAsync(view);
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__chip").Count > 0, TimeSpan.FromSeconds(5));

        var chip = view.FindAll(".cm-studio__chip")[0];
        await chip.ClickAsync();

        chip = view.FindAll(".cm-studio__chip")[0];
        Assert.Contains("cm-studio__chip--on", chip.ClassList);
        Assert.Equal("true", chip.GetAttribute("aria-pressed"), ignoreCase: true);
        Assert.StartsWith("Applied", chip.TextContent.Trim(), StringComparison.Ordinal);

        await chip.ClickAsync();
        chip = view.FindAll(".cm-studio__chip")[0];
        Assert.DoesNotContain("cm-studio__chip--on", chip.ClassList);
        Assert.Equal("false", chip.GetAttribute("aria-pressed"), ignoreCase: true);
    }

    [Fact]
    public async Task Content_rail_shows_distribution_items_not_internal_strategy_documents()
    {
        var artifacts = new List<ArtifactPreviewResponse>
        {
            Preview("blog", "Public article"),
            Preview("clip-suggestions", "Short-form clips"),
            Preview("campaign-summary", "Internal campaign summary"),
            Preview("seo-keyword-plan", "Internal keyword plan"),
            Preview("seo-brief", "Internal SEO brief"),
        };
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), artifacts, [], 0, 0));

        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__content-title").Count == 1, TimeSpan.FromSeconds(5));

        var titles = view.FindAll(".cm-studio__content-title").Select(node => node.TextContent).ToList();
        Assert.Contains("Public article", titles);
        Assert.DoesNotContain("Short-form clips", titles);
        Assert.DoesNotContain(titles, title => title.Contains("Internal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Selected_slot_and_take_dialog_show_the_actual_content_the_image_supports()
    {
        const string supportingCopy = "Virtualization keeps scrolling responsive while millions of rows remain available.";
        var blog = Preview("blog", "Enterprise grid performance") with { Id = BlogId };
        var slot = new ImageSlotResponse(
            SlotId, CampaignId, "blog-inline-2", 1200, 675,
            "Show a responsive data grid", "foundry", null, null, true,
            "Empty", null, null, DateTimeOffset.UtcNow, ArtifactId: BlogId);
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [blog], [slot], 0, 1));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{BlogId}",
            new ArtifactResponse(BlogId, CampaignId, "blog", "Enterprise grid performance",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    content = new
                    {
                        markdown = $"# Enterprise grid performance\n\nBefore section.\n\n{supportingCopy}\n\n![stub:blog-inline-2]()\n\nThe UI recycles row containers instead of rendering every record.",
                    },
                }),
                ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await OpenFirstSlotAsync(view);
        await view.WaitForAssertionAsync(() =>
            Assert.Contains(supportingCopy, view.Find(".cm-studio__context").TextContent,
                StringComparison.Ordinal));

        await view.Find(".cm-gallery__tile").ClickAsync();
        Assert.Contains(supportingCopy, view.Find(".cm-lightbox__context").TextContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_a_take_forever_asks_for_confirmation_then_removes_row_and_tile()
    {
        var confirm = new AutoConfirm(accept: true);
        Services.AddScoped<IConfirmService>(_ => confirm);
        Http.OnStatus(HttpMethod.Delete,
            $"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/variants/{TakeId}",
            System.Net.HttpStatusCode.NoContent);

        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await OpenFirstSlotAsync(view);
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-gallery__tile").Count == 1, TimeSpan.FromSeconds(5));

        await view.Find(".cm-gallery__tile").ClickAsync();
        await view.FindAll(".cm-lightbox button")
            .First(b => b.TextContent.Contains("Delete forever", StringComparison.Ordinal))
            .ClickAsync();

        Assert.Contains(confirm.Requests, r => r.Destructive
            && r.Message.Contains("no undo", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Http.Requests, r => r.Method == HttpMethod.Delete
            && r.RequestUri!.AbsolutePath.EndsWith($"variants/{TakeId}", StringComparison.Ordinal));
        Assert.Empty(view.FindAll(".cm-lightbox"));
        Assert.Empty(view.FindAll(".cm-gallery__tile"));
    }

    [Fact]
    public async Task A_declined_confirm_deletes_nothing()
    {
        var confirm = new AutoConfirm(accept: false);
        Services.AddScoped<IConfirmService>(_ => confirm);

        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await OpenFirstSlotAsync(view);
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-gallery__tile").Count == 1, TimeSpan.FromSeconds(5));

        await view.Find(".cm-gallery__tile").ClickAsync();
        await view.FindAll(".cm-lightbox button")
            .First(b => b.TextContent.Contains("Delete forever", StringComparison.Ordinal))
            .ClickAsync();

        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Single(view.FindAll(".cm-gallery__tile"));
    }

    // ---- helpers ---------------------------------------------------------------

    private sealed class AutoConfirm(bool accept) : IConfirmService
    {
        public List<ConfirmRequest> Requests { get; } = [];

        public Task<bool> ConfirmAsync(ConfirmRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(accept);
        }
    }


    /// <summary>The drawer is closed until a tile is chosen — open the first real slot tile.</summary>
    private static async Task OpenFirstSlotAsync(IRenderedComponent<ImageStudioView> view)
    {
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__card:not(.cm-studio__card--add)").Count > 0,
            TimeSpan.FromSeconds(5));
        await view.Find(".cm-studio__card:not(.cm-studio__card--add)").ClickAsync();
    }

    private void StubGallery(params ImageVariantResponse[] takes) =>
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/variants", takes.ToList());

    private void StubPatchResult(ImageVariantResponse take) =>
        Http.OnPatch($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/variants/{TakeId}", take);

    private int PreviewRequestCount() => Http.Requests.Count(request =>
        request.Method == HttpMethod.Get
        && request.RequestUri!.AbsolutePath.EndsWith($"campaigns/{CampaignId}/preview", StringComparison.Ordinal));

    private static ImageSlotResponse Slot(string? prompt = "a bold thumbnail", string promptMode = "Auto") =>
        new(SlotId, CampaignId, "youtube-thumbnail", 1280, 720,
            prompt, "gpt-image-2", null, null, true,
            "Empty", null, null, DateTimeOffset.UtcNow, PromptMode: promptMode);

    private static ImageVariantResponse Take(Guid id, string state) => new(
        id, SlotId,
        "https://public.example/campaigns/x/images/youtube-thumbnail/variants/1-full.webp",
        "https://public.example/campaigns/x/images/youtube-thumbnail/variants/thumbs/1.webp",
        "gpt-image-2", state, null, null, 1280, 720, DateTimeOffset.UtcNow);

    private static ArtifactPreviewResponse Preview(string kind, string title) =>
        new(Guid.NewGuid(), CampaignId, kind, title, ArtifactStatus.Draft, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);
}
