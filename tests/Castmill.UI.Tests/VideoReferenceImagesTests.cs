using Bunit;
using System.Net;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

public sealed class VideoReferenceImagesTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("99111111-1111-1111-1111-111111111111");
    private static readonly Guid BrandId = Guid.Parse("99222222-2222-2222-2222-222222222222");
    private static readonly Guid SourceId = Guid.Parse("99333333-3333-3333-3333-333333333333");
    private static readonly Guid AssetId = Guid.Parse("99444444-4444-4444-4444-444444444444");
    private static readonly Guid UploadId = Guid.Parse("99555555-5555-5555-5555-555555555555");
    private static readonly Guid VideoAssetId = Guid.Parse("99666666-6666-6666-6666-666666666666");

    public VideoReferenceImagesTests()
    {
        SignInTestUser();
        Media.EnableLocalProcessing("accessibility.mp4");
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", Preview());
        Http.OnGetQuery($"api/v1/campaigns/{CampaignId}/reference-sets?videoAssetId={SourceId}",
            new List<VideoReferenceSetResponse>());
        Http.OnGet($"api/v1/brands/{BrandId}/reference-crop-presets/",
            new List<ReferenceCropPresetResponse>());
        Http.OnPost("api/v1/assets", new AssetResponse(
            AssetId, "frame.png", "image/png", 8, $"assets/{AssetId:N}", DateTimeOffset.UtcNow));
        Http.OnStatus(HttpMethod.Post, $"api/v1/blob/assets/{AssetId}/content", HttpStatusCode.NoContent);
        Http.OnPost($"api/v1/campaigns/{CampaignId}/reference-sets", new VideoReferenceSetResponse(
            Guid.NewGuid(), BrandId, CampaignId, SourceId, "Video reference frames", null,
            "ui-demonstration", 60_000, 1920, 1080, 30, DateTimeOffset.UtcNow, []));
        Http.OnPost($"api/v1/campaigns/{CampaignId}/media-uploads", Upload("Uploading", 0, 0));
        Http.OnPut($"api/v1/campaigns/{CampaignId}/media-uploads/{UploadId}/blocks/0",
            Upload("Uploading", 1024, 1));
        Http.OnPost($"api/v1/campaigns/{CampaignId}/media-uploads/{UploadId}/commit",
            Upload("Committed", 1024, 1));
        Http.OnPatch($"api/v1/campaigns/{CampaignId}/sources/{SourceId}/media",
            Preview().Sources![0] with { MediaAssetId = VideoAssetId });
    }

    [Fact]
    public async Task Auto_select_exposes_v1_and_v11_controls_and_uses_local_analysis()
    {
        var view = Render<VideoReferenceImages>(parameters => parameters
            .Add(item => item.CampaignId, CampaignId));

        await view.WaitForStateAsync(() => view.FindAll("video").Count == 1, TimeSpan.FromSeconds(5));
        Assert.Contains("Extract exact frames", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Use AI to rank and suggest crop", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Manual capture", view.Markup, StringComparison.Ordinal);

        await view.FindAll("button").Single(button => button.TextContent.Trim() == "Analyze video").ClickAsync();

        await view.WaitForAssertionAsync(() => Assert.Equal(5, view.FindAll(".cm-reference-frame img").Count));
        Assert.Contains("Crop once", view.Markup, StringComparison.Ordinal);
        Assert.Contains("automatic · high", view.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clicking_an_extracted_frame_opens_a_full_resolution_lightbox()
    {
        var view = Render<VideoReferenceImages>(parameters => parameters
            .Add(item => item.CampaignId, CampaignId));
        await view.WaitForStateAsync(() => view.FindAll("video").Count == 1, TimeSpan.FromSeconds(5));
        await view.FindAll("button").Single(button => button.TextContent.Trim() == "Analyze video").ClickAsync();
        await view.WaitForStateAsync(() => view.FindAll(".cm-reference-frame-preview").Count == 5);

        await view.FindAll(".cm-reference-frame-preview")[0].ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            Assert.Single(view.FindAll(".cm-reference-lightbox"));
            Assert.StartsWith("data:image/png;base64,", view.Find(".cm-reference-lightbox__image").GetAttribute("src"));
            Assert.Contains("Frame 1", view.Find(".cm-reference-lightbox").TextContent, StringComparison.Ordinal);
        });
        Assert.Single(Media.RenderedFrames);

        await view.Find("button[aria-label='Close large image']").ClickAsync();
        Assert.Empty(view.FindAll(".cm-reference-lightbox"));
    }

    [Fact]
    public async Task Clicking_a_saved_reference_opens_the_original_asset_not_the_thumbnail()
    {
        var setId = Guid.Parse("99777777-7777-7777-7777-777777777777");
        var imageId = Guid.Parse("99888888-8888-8888-8888-888888888888");
        var savedAssetId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var savedImage = new VideoReferenceImageResponse(
            imageId, setId, SourceId, savedAssetId, savedAssetId, 15_000, 450,
            new VideoReferenceCropDto(0, 0, 1920, 1080, "none"), "automatic",
            "1234567890abcdef", 94, "Clear UI state", "Accessible data grid", 0,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Http.OnGetQuery($"api/v1/campaigns/{CampaignId}/reference-sets?videoAssetId={SourceId}",
            new List<VideoReferenceSetResponse>
            {
                new(setId, BrandId, CampaignId, SourceId, "Saved set", null,
                    "ui-demonstration", 60_000, 1920, 1080, 30, DateTimeOffset.UtcNow, [savedImage]),
            });
        Http.OnPost("api/v1/blob/assets/thumbs", new List<AssetThumb>
        {
            new(savedAssetId, "https://assets.example/frame-thumb.jpg", true),
        });
        Http.OnGet($"api/v1/blob/assets/{savedAssetId}/read-sas",
            new ReadSas("https://assets.example/frame-full.png"));

        var view = Render<VideoReferenceImages>(parameters => parameters
            .Add(item => item.CampaignId, CampaignId));
        await view.WaitForStateAsync(() => view.FindAll(".cm-reference-frame-preview").Count == 1,
            TimeSpan.FromSeconds(5));

        await view.Find(".cm-reference-frame-preview").ClickAsync();

        await view.WaitForAssertionAsync(() => Assert.Equal(
            "https://assets.example/frame-full.png",
            view.Find(".cm-reference-lightbox__image").GetAttribute("src")));
        Assert.Contains("Accessible data grid", view.Find(".cm-reference-lightbox").TextContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manual_keyboard_capture_adds_the_exact_current_frame()
    {
        var view = Render<VideoReferenceImages>(parameters => parameters
            .Add(item => item.CampaignId, CampaignId));
        await view.WaitForStateAsync(() => view.FindAll("video").Count == 1, TimeSpan.FromSeconds(5));

        await view.FindAll("button").Single(button => button.TextContent.Trim() == "Manual capture").ClickAsync();
        await view.Find(".cm-reference-page").KeyDownAsync(
            new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowRight" });
        await view.Find(".cm-reference-page").KeyDownAsync(
            new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "c" });

        await view.WaitForAssertionAsync(() => Assert.Single(view.FindAll(".cm-reference-frame img")));
        Assert.Contains("frame 1", view.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_uploads_lossless_full_frames_and_persists_timestamp_provenance()
    {
        var view = Render<VideoReferenceImages>(parameters => parameters
            .Add(item => item.CampaignId, CampaignId));
        await view.WaitForStateAsync(() => view.FindAll("video").Count == 1, TimeSpan.FromSeconds(5));
        await view.FindAll("button").Single(button => button.TextContent.Trim() == "Analyze video").ClickAsync();
        await view.WaitForStateAsync(() => view.FindAll(".cm-reference-frame img").Count == 5);
        await view.FindAll("button").Single(button => button.TextContent.Trim() == "Full frame").ClickAsync();
        await view.FindAll("button").Single(button => button.TextContent.Contains("Save 5 reference images", StringComparison.Ordinal)).ClickAsync();

        await view.WaitForAssertionAsync(() => Assert.Contains(Http.Bodies, body =>
            body.Method == HttpMethod.Post
            && body.Path.EndsWith("/reference-sets", StringComparison.Ordinal)
            && body.Body.Contains("sourceTimestampMs", StringComparison.OrdinalIgnoreCase)
            && body.Body.Contains("sourceFrameNumber", StringComparison.OrdinalIgnoreCase)
            && body.Body.Contains("\"method\":\"none\"", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(5, Media.RenderedFrames.Count);
        Assert.All(Media.RenderedFrames, rendered => Assert.Equal("none", rendered.Crop.Method));
        Assert.Contains(Http.Bodies, body => body.Method == HttpMethod.Post
            && body.Path.EndsWith("/media-uploads", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Choosing_a_video_without_an_existing_transcript_attaches_it_to_the_campaign()
    {
        var preview = Preview() with { Sources = [] };
        var now = DateTimeOffset.UtcNow;
        var attached = new SourceAssetResponse(
            SourceId, CampaignId, null, SourceKinds.Video, SourceModalities.Media,
            "accessibility.mp4", null, "video/mp4", 1024, "sha256:test", 1, Guid.NewGuid(),
            null, now, now, "/tmp/accessibility.mp4", "test-video-hash", null, "video/mp4");
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", preview);
        Http.OnGet($"api/v1/campaigns/{CampaignId}/reference-sets",
            new List<VideoReferenceSetResponse>());
        Http.OnPost($"api/v1/campaigns/{CampaignId}/sources/media", attached);

        var view = Render<VideoReferenceImages>(parameters => parameters
            .Add(item => item.CampaignId, CampaignId));
        await view.WaitForStateAsync(() => view.FindAll(".cm-reference-drop").Count == 1,
            TimeSpan.FromSeconds(5));

        await view.Find(".cm-reference-drop button").ClickAsync();

        await view.WaitForAssertionAsync(() => Assert.Contains(Http.Bodies, body =>
            body.Method == HttpMethod.Post
            && body.Path.EndsWith("/sources/media", StringComparison.Ordinal)
            && body.Body.Contains("accessibility.mp4", StringComparison.Ordinal)));
        Assert.Single(view.FindAll("video.cm-reference-video"));
    }

    private static CampaignPreview Preview()
    {
        var now = DateTimeOffset.UtcNow;
        var revisionId = Guid.NewGuid();
        var source = new SourceAssetResponse(
            SourceId, CampaignId, null, SourceKinds.Transcript, SourceModalities.Media,
            "Accessibility demo", null, "video/mp4", 1000, "sha256:test", 1, revisionId,
            new ApprovedEvidenceRevision(SourceId, 1, revisionId, "approved", now), now, now,
            "/tmp/accessibility.mp4", "test-video-hash", null, "video/mp4");
        var campaign = new CampaignResponse(CampaignId, Guid.NewGuid(), "Accessibility campaign", null,
            now, now, BrandId);
        return new CampaignPreview(campaign, [], [], 0, 0, null, [source]);
    }

    private static MediaUploadResponse Upload(string status, long uploaded, int nextBlock) => new(
        UploadId, CampaignId, VideoAssetId, "accessibility.mp4", "video/mp4", 1024,
        uploaded, nextBlock, 4 * 1024 * 1024, status, null, null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7));
}
