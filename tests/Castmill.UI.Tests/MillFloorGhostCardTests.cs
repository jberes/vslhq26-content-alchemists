using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;
using Castmill.UI.State;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// Item 2 of the UX overhaul: a press run must always be visible ON THE BOARD, not only in
/// the press panel — each in-flight kind renders a ghost card in its lane, pulsing until
/// the real card lands (ADR-F13: progress, never a dead surface).
/// </summary>
public sealed class MillFloorGhostCardTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("51111111-1111-1111-1111-111111111111");
    private static readonly Guid TranscriptId = Guid.Parse("51111111-1111-1111-1111-222222222222");

    public MillFloorGhostCardTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        StubPreview(Artifact(TranscriptId, "transcript", "Source transcript"));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{TranscriptId}", new ArtifactResponse(
            TranscriptId, CampaignId, "transcript", "Source transcript",
            """{"source":"test","segments":[{"id":"s01","text":"Hello","startSeconds":0,"endSeconds":2}]}""",
            ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_running_press_renders_a_ghost_card_in_the_correct_lane_for_each_pending_kind()
    {
        // The generate POST hangs — the model is "still working".
        Http.Gate(HttpMethod.Post, $"api/v1/ai/campaigns/{CampaignId}/generate");

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Print more from this source");

        var press = Services.GetRequiredService<PressRunService>();
        await view.InvokeAsync(() => press.Start(CampaignId, TranscriptId, null, ["blog", "social-x"]));

        await view.WaitForStateAsync(
            () => view.FindAll(".cm-card--ghost").Count == 2, TimeSpan.FromSeconds(5));

        // Each ghost sits in its own lane and says what it is printing.
        var blogLane = view.FindAll(".cm-lane")
            .First(l => l.QuerySelector(".cm-lane__label")!.TextContent.Trim() == "Blog");
        Assert.NotNull(blogLane.QuerySelector(".cm-card--ghost"));
        Assert.Contains("Blog post", blogLane.TextContent, StringComparison.Ordinal);

        var socialLane = view.FindAll(".cm-lane")
            .First(l => l.QuerySelector(".cm-lane__label")!.TextContent.Trim() == "Social");
        Assert.NotNull(socialLane.QuerySelector(".cm-card--ghost"));

        // Ghosts never carry data-card — the provenance overlay must not measure them.
        Assert.All(view.FindAll(".cm-card--ghost"), g => Assert.Null(g.GetAttribute("data-card")));

        // The pulse dot is present: the "AI is working" animation.
        Assert.NotEmpty(view.FindAll(".cm-card--ghost .cm-ai-dot"));
    }

    [Fact]
    public async Task Kinds_that_never_render_on_the_board_get_no_ghost()
    {
        Http.Gate(HttpMethod.Post, $"api/v1/ai/campaigns/{CampaignId}/generate");

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Print more from this source");

        var press = Services.GetRequiredService<PressRunService>();
        await view.InvokeAsync(() => press.Start(CampaignId, TranscriptId, null, ["image-prompts", "blog"]));

        await view.WaitForStateAsync(
            () => view.FindAll(".cm-card--ghost").Count == 1, TimeSpan.FromSeconds(5));

        Assert.DoesNotContain("Image prompts", string.Join(" ",
            view.FindAll(".cm-card--ghost").Select(g => g.TextContent)));
    }

    [Fact]
    public async Task The_run_ending_reconciles_the_board_with_one_forced_reload()
    {
        var gate = Http.Gate(HttpMethod.Post, $"api/v1/ai/campaigns/{CampaignId}/generate");

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Print more from this source");

        var press = Services.GetRequiredService<PressRunService>();
        await view.InvokeAsync(() => press.Start(CampaignId, TranscriptId, null, ["blog"]));

        await view.WaitForStateAsync(
            () => view.FindAll(".cm-card--ghost").Count == 1, TimeSpan.FromSeconds(5));

        var previewsBefore = PreviewRequestCount();

        // The generated blog is on the server now; the run completes.
        StubPreview(
            Artifact(TranscriptId, "transcript", "Source transcript"),
            Artifact(Guid.NewGuid(), "blog", "Fresh off the press"));
        gate.SetResult(StubHttpHandler.Json(new RunFinished(
            Guid.NewGuid(), 1, 0,
            [new RunItem("blog", true, Guid.NewGuid(), null, null, 1200)])));

        // The service's reconciliation reload brings the real card in and retires the ghost.
        await view.WaitForStateAsync(
            () => view.Markup.Contains("Fresh off the press", StringComparison.Ordinal)
                && view.FindAll(".cm-card--ghost").Count == 0,
            TimeSpan.FromSeconds(5));

        Assert.True(PreviewRequestCount() > previewsBefore,
            "the run's end must force a preview reload even without a poll completion");
    }

    // ---- helpers ---------------------------------------------------------------

    private int PreviewRequestCount() => Http.Requests.Count(r =>
        r.Method == HttpMethod.Get
        && r.RequestUri!.AbsolutePath.EndsWith("/preview", StringComparison.Ordinal));

    private void StubPreview(params ArtifactPreviewResponse[] artifacts) =>
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(), artifacts, [], 0, 0));

    private static async Task WaitForTextAsync(IRenderedComponent<MillFloorView> view, string text)
    {
        try
        {
            await view.WaitForStateAsync(
                () => view.Markup.Contains(text, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            Assert.Fail($"'{text}' never rendered ({ex.GetType().Name}). Markup was:{Environment.NewLine}{view.Markup}");
        }
    }

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);

    private static ArtifactPreviewResponse Artifact(Guid id, string kind, string title) =>
        new(id, CampaignId, kind, title, ArtifactStatus.Draft, 1,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
}
