using Bunit;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// The Mill Floor's source recording (ADR-057): a real player when a copy is reachable, an
/// honest state when it is not, and never a decorative play glyph. The test shell cannot
/// serve local files, so the local-only source shows why it will not play here.
/// </summary>
public sealed class MillFloorMediaTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("c4444444-1111-1111-1111-111111111111");
    private static readonly Guid TranscriptId = Guid.Parse("c4444444-1111-1111-1111-222222222222");
    private static readonly Guid AssetId = Guid.Parse("c4444444-1111-1111-1111-333333333333");
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public MillFloorMediaTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{TranscriptId}", new ArtifactResponse(
            TranscriptId, CampaignId, "transcript", "Recording", System.Text.Json.JsonSerializer.Serialize(
                new TranscriptContent("webinar.mp4", [new TranscriptSegment("S1", 0, 4.5, null, "Welcome to the webinar."), new TranscriptSegment("S2", 4.5, 9, null, "Today we ship.")]),
                Json),
            ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_cloud_copy_plays_through_a_read_sas_and_the_transcript_rows_are_seekable()
    {
        var source = TranscriptSource(localPath: null, mediaAssetId: AssetId, contentType: "video/mp4");
        StubPreview(source);
        Http.OnGet($"api/v1/blob/assets/{AssetId}/read-sas", new ReadSas("https://sas.example/webinar.mp4?sig=1"));

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));

        await view.WaitForAssertionAsync(() =>
            Assert.Equal("https://sas.example/webinar.mp4?sig=1", view.Find("video.cm-source__media").GetAttribute("src")));
        Assert.Contains("CLOUD COPY", view.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(view.FindAll(".cm-seg--seekable"));
        // The test shell cannot play local files, so no Locate/Upload actions are offered.
        Assert.DoesNotContain("Locate file", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_local_only_recording_explains_why_it_does_not_play_on_this_shell()
    {
        StubPreview(TranscriptSource(localPath: "/Users/jason/Movies/webinar.mp4", mediaAssetId: null, contentType: "video/mp4"));

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));

        await view.WaitForAssertionAsync(() =>
            Assert.Contains("Recording lives on the machine that transcribed it.", view.Markup, StringComparison.Ordinal));
        Assert.Empty(view.FindAll("video, audio"));
        Assert.Contains("webinar.mp4", view.Find(".cm-source__path").TextContent, StringComparison.Ordinal);
    }

    private void StubPreview(SourceAssetResponse source) =>
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(),
            [new ArtifactPreviewResponse(TranscriptId, CampaignId, "transcript", "Recording", ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)],
            [], 0, 0, Sources: [source]));

    private static SourceAssetResponse TranscriptSource(string? localPath, Guid? mediaAssetId, string contentType) => new(
        Guid.NewGuid(), CampaignId, TranscriptId, SourceKinds.Transcript, SourceModalities.Media, "webinar.mp4",
        null, contentType, 1_000_000, "sha256:abc", 1, Guid.NewGuid(), null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        LocalPath: localPath, ContentHash: "sz1000000-abc", MediaAssetId: mediaAssetId, ContentTypeHint: contentType);

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Webinar", null, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);
}
