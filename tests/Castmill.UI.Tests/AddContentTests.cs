using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// Adding more of a kind the campaign already has. Nothing on the server had to change to
/// allow it — every generate already inserts a new row — but the board used to hide the chip
/// the moment you owned one, and the on-demand path hard-coded a null brief, so "one more
/// blog, about pricing" had nowhere to be said.
/// </summary>
public sealed class AddContentTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("a1111111-1111-1111-1111-111111111111");

    public AddContentTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        StubPreview(Artifact("transcript", "Source transcript"), Artifact("blog", "The first blog"));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{TranscriptId}", new ArtifactResponse(
            TranscriptId, CampaignId, "transcript", "Source transcript",
            """{"source":"paste","segments":[{"id":"S1","startSeconds":0,"endSeconds":2,"text":"Hi."}]}""",
            ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Choosing_a_kind_opens_an_angle_and_count_panel()
    {
        var view = await OpenAsync();

        Assert.Empty(view.FindAll(".cm-board__add"));

        await ChipAsync(view, "Blog").ClickAsync();

        Assert.NotNull(view.Find(".cm-board__add"));
        Assert.Contains("Angle for Blog", view.Markup, StringComparison.Ordinal);
        // It already has one, so the verb is "Add", not "Print".
        Assert.Contains(view.FindAll(".cm-board__add button"),
            b => b.TextContent.Contains("Add 1 blog", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_kind_the_campaign_lacks_reads_as_print_not_add()
    {
        var view = await OpenAsync();
        await ChipAsync(view, "Newsletter").ClickAsync();

        Assert.Contains(view.FindAll(".cm-board__add button"),
            b => b.TextContent.Contains("Print 1 newsletter", StringComparison.Ordinal));
    }

    /// <summary>
    /// The point of the whole feature: the angle becomes the run's brief and the count
    /// becomes a real count — kinds is a set server-side, so three LinkedIn posts cannot be
    /// asked for by repeating the kind three times.
    /// </summary>
    [Fact]
    public async Task The_angle_and_the_count_reach_the_generate_request()
    {
        var view = await OpenAsync();
        await ChipAsync(view, "Blog").ClickAsync();

        view.Find(".cm-board__add input.cm-input").Input("Make this one about pricing objections.");
        view.FindAll(".cm-board__add-count input[type=radio]")[2].Change(true); // 1, 2, 3, 5 → 3

        Http.OnPost($"api/v1/ai/campaigns/{CampaignId}/generate",
            new RunFinished(Guid.NewGuid(), 3, 0, []));

        await view.FindAll(".cm-board__add button")
            .First(b => b.TextContent.Contains("Add 3 blog posts", StringComparison.Ordinal))
            .ClickAsync();

        await view.WaitForStateAsync(
            () => Http.Bodies.Any(b => b.Path.EndsWith("/generate", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));

        var body = Http.Bodies.Last(b => b.Path.EndsWith("/generate", StringComparison.Ordinal)).Body;
        Assert.Contains("Make this one about pricing objections.", body, StringComparison.Ordinal);
        Assert.Contains("\"count\":3", body, StringComparison.Ordinal);
        Assert.Contains("\"blog\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_closes_the_panel_without_generating()
    {
        var view = await OpenAsync();
        await ChipAsync(view, "Blog").ClickAsync();

        await view.FindAll(".cm-board__add button")
            .First(b => b.TextContent.Contains("Cancel", StringComparison.Ordinal))
            .ClickAsync();

        Assert.Empty(view.FindAll(".cm-board__add"));
        Assert.DoesNotContain(Http.Requests, r =>
            r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/generate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Copy_and_view_copy_all_put_the_complete_transcript_on_the_clipboard()
    {
        var view = await OpenAsync();

        await view.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Copy")
            .ClickAsync();

        Assert.Equal(["Hi."], Clipboard.Copies);

        await view.FindAll("button")
            .Single(button => button.TextContent.Trim() == "View")
            .ClickAsync();
        await view.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Copy all")
            .ClickAsync();

        Assert.Equal(["Hi.", "Hi."], Clipboard.Copies);
    }

    // ---- helpers ---------------------------------------------------------------

    private async Task<IRenderedComponent<MillFloorView>> OpenAsync()
    {
        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.Markup.Contains("Print more from this source", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        return view;
    }

    private static AngleSharp.Dom.IElement ChipAsync(IRenderedComponent<MillFloorView> view, string label) =>
        view.FindAll(".cm-print-chip").First(c => c.TextContent.Contains(label, StringComparison.Ordinal));

    private static readonly Guid TranscriptId = Guid.Parse("a1111111-1111-1111-1111-999999999999");

    private void StubPreview(params ArtifactPreviewResponse[] artifacts) =>
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [.. artifacts], [], 0, 0));

    private static ArtifactPreviewResponse Artifact(string kind, string title) =>
        new(kind == "transcript" ? TranscriptId : Guid.NewGuid(), CampaignId, kind, title,
            ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);
}
