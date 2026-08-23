using System.Reflection;
using System.Text.Json;
using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages;
using Microsoft.AspNetCore.Components.Forms;

namespace Castmill.UI.Tests;

public sealed class N28MediaUploadTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId =
        Guid.Parse("92800000-0000-0000-0000-000000000001");
    private static readonly Guid UploadId =
        Guid.Parse("92800000-0000-0000-0000-000000000002");
    private static readonly Guid AssetId =
        Guid.Parse("92800000-0000-0000-0000-000000000003");
    private static readonly Guid TranscriptId =
        Guid.Parse("92800000-0000-0000-0000-000000000004");
    private static readonly Guid SourceId =
        Guid.Parse("92800000-0000-0000-0000-000000000005");
    private static readonly Guid RevisionId =
        Guid.Parse("92800000-0000-0000-0000-000000000006");
    private static readonly byte[] Audio = "recorded voice bytes"u8.ToArray();

    [Fact]
    public async Task Cloud_media_upload_transcribes_and_opens_timed_evidence_review()
    {
        ArrangeBase();
        Http.OnPost("api/v1/campaigns", Campaign());
        Http.OnPost(
            $"api/v1/campaigns/{CampaignId}/media-uploads",
            Upload(MediaUploadStatus.Uploading, 0, 0));
        Http.OnPut(
            $"api/v1/campaigns/{CampaignId}/media-uploads/{UploadId}/blocks/0",
            Upload(MediaUploadStatus.Uploading, Audio.Length, 1));
        Http.OnPost(
            $"api/v1/campaigns/{CampaignId}/media-uploads/{UploadId}/commit",
            Upload(MediaUploadStatus.Committed, Audio.Length, 1));
        Http.OnPost(
            $"api/v1/campaigns/{CampaignId}/media-uploads/{UploadId}/transcribe",
            Upload(MediaUploadStatus.Completed, Audio.Length, 1) with
            {
                TranscriptArtifactId = TranscriptId,
            });
        Http.OnGet(
            $"api/v1/campaigns/{CampaignId}/sources",
            new List<SourceAssetResponse> { Source() });
        Http.OnGetQuery(
            $"api/v1/campaigns/{CampaignId}/sources/{SourceId}/evidence?approved=false",
            Evidence());
        var view = Render<NewCampaign>();
        SetCloudMedia(view, new TestBrowserFile("voice.webm", "audio/webm", Audio));

        await InvokePrivateAsync(view, "StartCloudMediaIngestAsync");

        view.WaitForAssertion(() =>
            Assert.Contains("Choose the campaign intent", view.Markup, StringComparison.Ordinal));
        Assert.Contains("Timed voice proof", view.Find(".cm-run__source-summary").TextContent,
            StringComparison.Ordinal);
        Assert.Empty(view.FindAll(".cm-evidence-review"));
        view.FindAll("button").Single(button => button.TextContent.Trim() == "Review transcript").Click();
        Assert.Contains("Timed voice proof", view.Find(".cm-evidence-review").TextContent,
            StringComparison.Ordinal);
        Assert.Contains(Http.Bodies, request =>
            request.Method == HttpMethod.Put
            && request.Path.EndsWith("/blocks/0", StringComparison.Ordinal)
            && request.Body == System.Text.Encoding.UTF8.GetString(Audio));
        Assert.Contains(Http.Bodies, request =>
            request.Method == HttpMethod.Post
            && request.Path.EndsWith("/transcribe", StringComparison.Ordinal));
        Assert.Equal(100, PrivateField<int>(view, "_percent"));
    }

    [Fact]
    public async Task Reload_recovers_upload_offset_and_requires_reselecting_the_same_file()
    {
        ArrangeBase();
        Http.OnGet($"api/v1/campaigns/{CampaignId}", Campaign());
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(), [], [], 0, 0, Sources: []));
        Http.OnGet(
            $"api/v1/campaigns/{CampaignId}/media-uploads/latest",
            Upload(MediaUploadStatus.Uploading, 8, 1));
        var view = Render<NewCampaign>();

        await InvokePrivateAsync(view, "ResumeAsync", CampaignId);
        view.Render();

        Assert.Contains("Reselect voice.webm", view.Markup, StringComparison.Ordinal);
        Assert.Contains("resume at", view.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Upload media", view.Markup, StringComparison.Ordinal);
        Assert.Equal(8, PrivateField<MediaUploadResponse>(view, "_mediaUpload").UploadedBytes);
    }

    [Fact]
    public void Desktop_retains_local_whisper_alongside_cloud_upload()
    {
        ArrangeBase();
        Media.EnableLocalProcessing();

        var view = Render<NewCampaign>();
        var starters = view.FindAll(".cm-starter").Select(item => item.TextContent).ToList();

        Assert.Contains(starters, text => text.Contains("Upload media", StringComparison.Ordinal));
        Assert.Contains(starters, text => text.Contains("Local media", StringComparison.Ordinal));
    }

    private void ArrangeBase()
    {
        SignInTestUser();
        Http.OnGet("api/v1/brands", new List<BrandProfileDetailResponse>());
    }

    private static CampaignResponse Campaign() => new(
        CampaignId,
        Guid.NewGuid(),
        "Voice campaign",
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        ContentType: CampaignContentType.ThoughtLeadership);

    private static MediaUploadResponse Upload(
        string status, long uploadedBytes, int nextBlock) => new(
        UploadId,
        CampaignId,
        AssetId,
        "voice.webm",
        "audio/webm",
        Audio.Length,
        uploadedBytes,
        nextBlock,
        4 * 1024 * 1024,
        status,
        null,
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddDays(7));

    private static SourceAssetResponse Source()
    {
        var now = DateTimeOffset.UtcNow;
        return new SourceAssetResponse(
            SourceId,
            CampaignId,
            TranscriptId,
            SourceKinds.Transcript,
            SourceModalities.Media,
            "voice.webm",
            null,
            "audio/webm",
            Audio.Length,
            "sha256:voice",
            1,
            RevisionId,
            new ApprovedEvidenceRevision(SourceId, 1, RevisionId, "approved", now),
            now,
            now);
    }

    private static EvidenceRevisionResponse Evidence()
    {
        using var locator = JsonDocument.Parse(
            """{"startSeconds":0,"endSeconds":2.5,"speaker":"Host","sourceLabel":"voice.webm"}""");
        return new EvidenceRevisionResponse(
            Source(),
            1,
            RevisionId,
            true,
            [new EvidenceBlockResponse(
                SourceId,
                "s01",
                0,
                "Timed voice proof",
                EvidenceLocatorKinds.MediaTimeRange,
                locator.RootElement.Clone(),
                1,
                RevisionId,
                EvidenceApprovalStates.Approved,
                false)]);
    }

    private static void SetCloudMedia(
        IRenderedComponent<NewCampaign> view, IBrowserFile file)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(NewCampaign).GetField("_name", flags)!.SetValue(view.Instance, "Voice campaign");
        typeof(NewCampaign).GetField("_mediaFile", flags)!.SetValue(view.Instance, file);
        var starter = typeof(NewCampaign).GetField("_sourceStarter", flags)!;
        starter.SetValue(view.Instance, Enum.Parse(starter.FieldType, "CloudMedia"));
        view.Render();
    }

    private static T PrivateField<T>(
        IRenderedComponent<NewCampaign> view, string fieldName) =>
        (T)typeof(NewCampaign).GetField(
            fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view.Instance)!;

    private static async Task InvokePrivateAsync(
        IRenderedComponent<NewCampaign> view, string methodName, params object[] args)
    {
        await view.InvokeAsync(async () =>
        {
            var method = typeof(NewCampaign).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Instance)!;
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
            CancellationToken cancellationToken = default)
        {
            if (bytes.LongLength > maxAllowedSize)
            {
                throw new IOException("File exceeds max size.");
            }
            return new MemoryStream(bytes, writable: false);
        }
    }
}