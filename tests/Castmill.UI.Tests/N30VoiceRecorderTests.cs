using System.Reflection;
using System.Text.Json;
using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Castmill.UI.Http;
using Castmill.UI.Pages;
using Castmill.UI.Platform;

namespace Castmill.UI.Tests;

public sealed class N30VoiceRecorderTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId =
        Guid.Parse("93000000-0000-0000-0000-000000000001");
    private static readonly Guid UploadId =
        Guid.Parse("93000000-0000-0000-0000-000000000002");
    private static readonly Guid AssetId =
        Guid.Parse("93000000-0000-0000-0000-000000000003");
    private static readonly Guid TranscriptId =
        Guid.Parse("93000000-0000-0000-0000-000000000004");
    private static readonly Guid SourceId =
        Guid.Parse("93000000-0000-0000-0000-000000000005");
    private static readonly Guid RevisionId =
        Guid.Parse("93000000-0000-0000-0000-000000000006");

    [Fact]
    public void Recorder_does_not_request_microphone_before_the_record_gesture()
    {
        var view = Render<VoiceRecorder>();

        Assert.Equal(1, Voice.InitializeCalls);
        Assert.Equal(0, Voice.StartCalls);
        view.FindAll("button").Single(button => button.TextContent.Trim() == "Record").Click();
        Assert.Equal(1, Voice.StartCalls);
        Assert.Contains("Recording", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Recorder_surfaces_unsupported_and_permission_denied_states()
    {
        Voice.Set(new VoiceCaptureSnapshot(
            VoiceCaptureStates.Unsupported,
            Message: "Voice recording requires HTTPS or localhost."));
        var unsupported = Render<VoiceRecorder>();
        Assert.Contains("requires HTTPS or localhost", unsupported.Markup, StringComparison.Ordinal);
        Assert.Empty(unsupported.FindAll("button"));

        Voice.Set(new VoiceCaptureSnapshot(
            VoiceCaptureStates.PermissionDenied,
            Message: "Microphone access was denied."));
        var denied = Render<VoiceRecorder>();
        Assert.Contains("Microphone access was denied", denied.Markup, StringComparison.Ordinal);
        Assert.Contains(denied.FindAll("button"), button =>
            button.TextContent.Contains("Try microphone again", StringComparison.Ordinal));
    }

    [Fact]
    public void Start_a_run_hides_unsupported_voice_control_and_states_the_reason()
    {
        Voice.Set(new VoiceCaptureSnapshot(
            VoiceCaptureStates.Unsupported,
            Message: "Voice recording requires HTTPS or localhost."));
        SignInTestUser();
        Http.OnGet("api/v1/brands", new List<BrandProfileDetailResponse>());

        var view = Render<NewCampaign>();

        Assert.DoesNotContain(view.FindAll(".cm-starter"), starter =>
            starter.TextContent.Contains("Record an idea", StringComparison.Ordinal));
        Assert.Contains("Voice recording unavailable", view.Markup, StringComparison.Ordinal);
        Assert.Contains("requires HTTPS or localhost", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Recorder_exposes_level_pause_resume_stop_playback_and_discard()
    {
        var view = Render<VoiceRecorder>();
        Voice.Set(new VoiceCaptureSnapshot(
            VoiceCaptureStates.Recording,
            4.2,
            0.64,
            ContentType: "audio/webm"));
        view.WaitForAssertion(() => Assert.Equal(
            "64",
            view.Find("[role=meter]").GetAttribute("aria-valuenow")));
        Assert.Contains("00:04", view.Markup, StringComparison.Ordinal);

        view.FindAll("button").Single(button => button.TextContent.Trim() == "Pause").Click();
        Assert.Equal(1, Voice.PauseCalls);
        view.FindAll("button").Single(button => button.TextContent.Trim() == "Resume").Click();
        Assert.Equal(1, Voice.ResumeCalls);
        view.FindAll("button").Single(button => button.TextContent.Trim() == "Stop").Click();
        Assert.Equal(1, Voice.StopCalls);
        Assert.Equal("blob:test-voice", view.Find("audio").GetAttribute("src"));
        view.FindAll("button").Single(button => button.TextContent.Trim() == "Discard").Click();
        Assert.Equal(1, Voice.DiscardCalls);
        Assert.Contains("Record", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Use_recording_follows_the_resumable_upload_and_timed_evidence_path()
    {
        SignInTestUser();
        Http.OnGet("api/v1/brands", new List<BrandProfileDetailResponse>());
        Http.OnPost("api/v1/campaigns", Campaign());
        Http.OnPost(
            $"api/v1/campaigns/{CampaignId}/media-uploads",
            Upload(MediaUploadStatus.Uploading, 0, 0));
        Http.OnPut(
            $"api/v1/campaigns/{CampaignId}/media-uploads/{UploadId}/blocks/0",
            Upload(MediaUploadStatus.Uploading, Voice.Recording.Bytes.Length, 1));
        Http.OnPost(
            $"api/v1/campaigns/{CampaignId}/media-uploads/{UploadId}/commit",
            Upload(MediaUploadStatus.Committed, Voice.Recording.Bytes.Length, 1));
        Http.OnPost(
            $"api/v1/campaigns/{CampaignId}/media-uploads/{UploadId}/transcribe",
            Upload(MediaUploadStatus.Completed, Voice.Recording.Bytes.Length, 1) with
            {
                TranscriptArtifactId = TranscriptId,
            });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/sources", new List<SourceAssetResponse> { Source() });
        Http.OnGetQuery(
            $"api/v1/campaigns/{CampaignId}/sources/{SourceId}/evidence?approved=false",
            Evidence());
        var view = Render<NewCampaign>();
        SetVoiceStarter(view);
        Voice.Set(new VoiceCaptureSnapshot(
            VoiceCaptureStates.Stopped,
            Voice.Recording.Duration.TotalSeconds,
            PlaybackUrl: Voice.Recording.PlaybackUrl,
            ContentType: Voice.Recording.ContentType,
            SizeBytes: Voice.Recording.Bytes.Length));

        await view.FindAll("button").Single(button => button.TextContent.Trim() == "Use recording").ClickAsync();

        await view.WaitForAssertionAsync(() =>
            Assert.Contains("Choose the campaign intent", view.Markup, StringComparison.Ordinal));
        Assert.Equal(1, Voice.UseCalls);
        Assert.Contains("Recorded evidence", view.Markup, StringComparison.Ordinal);
        Assert.Empty(view.FindAll(".cm-evidence-review"));
        await view.FindAll("button").Single(button => button.TextContent.Trim() == "Review transcript").ClickAsync();
        await view.WaitForAssertionAsync(() =>
            Assert.Contains("Recorded evidence", view.Find(".cm-evidence-review").TextContent,
                StringComparison.Ordinal));
        Assert.Contains(Http.Bodies, request =>
            request.Method == HttpMethod.Post
            && request.Path.EndsWith("/media-uploads", StringComparison.Ordinal)
            && request.Body.Contains("voice-note.webm", StringComparison.Ordinal));
    }

    private static CampaignResponse Campaign() => new(
        CampaignId,
        Guid.NewGuid(),
        "Voice note campaign",
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        ContentType: CampaignContentType.ThoughtLeadership);

    private MediaUploadResponse Upload(string status, long uploaded, int block) => new(
        UploadId,
        CampaignId,
        AssetId,
        Voice.Recording.FileName,
        Voice.Recording.ContentType,
        Voice.Recording.Bytes.Length,
        uploaded,
        block,
        4 * 1024 * 1024,
        status,
        null,
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddDays(7));

    private SourceAssetResponse Source()
    {
        var now = DateTimeOffset.UtcNow;
        return new SourceAssetResponse(
            SourceId,
            CampaignId,
            TranscriptId,
            SourceKinds.Transcript,
            SourceModalities.Media,
            Voice.Recording.FileName,
            null,
            Voice.Recording.ContentType,
            Voice.Recording.Bytes.Length,
            "sha256:recording",
            1,
            RevisionId,
            new ApprovedEvidenceRevision(SourceId, 1, RevisionId, "approved", now),
            now,
            now);
    }

    private EvidenceRevisionResponse Evidence()
    {
        using var locator = JsonDocument.Parse(
            $$"""{"startSeconds":0,"endSeconds":4,"speaker":null,"sourceLabel":"{{Voice.Recording.FileName}}"}""");
        return new EvidenceRevisionResponse(
            Source(),
            1,
            RevisionId,
            true,
            [new EvidenceBlockResponse(
                SourceId,
                "s01",
                0,
                "Recorded evidence",
                EvidenceLocatorKinds.MediaTimeRange,
                locator.RootElement.Clone(),
                1,
                RevisionId,
                EvidenceApprovalStates.Approved,
                false)]);
    }

    private static void SetVoiceStarter(IRenderedComponent<NewCampaign> view)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var starter = typeof(NewCampaign).GetField("_sourceStarter", flags)!;
        starter.SetValue(view.Instance, Enum.Parse(starter.FieldType, "VoiceNote"));
        view.Render();
    }
}