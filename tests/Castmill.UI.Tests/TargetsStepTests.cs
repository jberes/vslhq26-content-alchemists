using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages;

namespace Castmill.UI.Tests;

/// <summary>
/// The Targets step. Its whole reason to exist is that keyword research used to run AFTER the
/// fan-out, as a report about content already written — so nothing was ever aimed at anything.
///
/// Two properties matter and are easy to lose: the low-friction default (the best three
/// pre-selected so "Press Run" stays one click), and that research failing never blocks a run.
/// </summary>
public sealed class TargetsStepTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid TranscriptId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static SeoResearchResponse Research() => new(
        [
            new SeoTarget("react data grid", 8100, 42, 157.7, "provider"),
            new SeoTarget("react table component", 2400, 31, 58.5, "provider"),
            new SeoTarget("react grid excel export", 590, 18, 21.1, "provider"),
            new SeoTarget("react pivot grid", 320, 22, 10.0, "provider"),
        ],
        [
            new SeoQuestion("How do you paginate a React data grid?", "paa"),
            new SeoQuestion("Can React handle a million rows?", "paa"),
        ],
        HasProviderMetrics: true,
        Notes: []);

    private void ArrangeRun()
    {
        SignInTestUser();
        Http.OnGet("api/v1/brands", new List<BrandProfileDetailResponse>());
        Http.OnPost("api/v1/seo/research", Research());
    }

    [Fact]
    public async Task The_best_three_keywords_are_preselected_so_press_run_stays_one_click()
    {
        ArrangeRun();
        var view = Render<NewCampaign>();

        await EnterTargetsAsync(view);

        // The keyword list is the first .cm-targets block; questions are the second.
        var keywordBoxes = view.FindAll(".cm-targets")[0].QuerySelectorAll("input[type=checkbox]");

        // Three of four, not all and not none: the careful path is available, not compulsory,
        // and an empty default would make the step a chore.
        Assert.Equal(4, keywordBoxes.Length);
        Assert.Equal(3, keywordBoxes.Count(c => c.HasAttribute("checked")));

        // The strongest keyword is primary out of the box — and the selected state is its own
        // control class, because the chip variant lost a specificity fight with
        // `button.cm-chip` and rendered grey-on-grey.
        var primary = view.FindAll(".cm-primary-pick--on").Single();
        Assert.Contains("Primary", primary.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Questions_from_people_also_ask_are_labelled_as_such()
    {
        ArrangeRun();
        var view = Render<NewCampaign>();

        await EnterTargetsAsync(view);

        // Provenance is the point: a question Google actually shows is worth more than one a
        // model imagined, and the user should be able to tell them apart.
        Assert.Contains("people also ask", view.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("How do you paginate a React data grid?", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Research_failing_leaves_the_run_available_rather_than_blocking_it()
    {
        SignInTestUser();
        Http.OnGet("api/v1/brands", new List<BrandProfileDetailResponse>());
        Http.OnStatus(HttpMethod.Post, "api/v1/seo/research", System.Net.HttpStatusCode.ServiceUnavailable);

        var view = Render<NewCampaign>();
        await EnterTargetsAsync(view);

        // Targets are an improvement, not a gate.
        Assert.Contains("run without targets", view.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(view.FindAll("button"),
            b => b.TextContent.Contains("Press Run", StringComparison.Ordinal) && !b.HasAttribute("disabled"));
    }

    [Fact]
    public async Task With_no_provider_metrics_the_step_says_so_instead_of_showing_zeroes()
    {
        SignInTestUser();
        Http.OnGet("api/v1/brands", new List<BrandProfileDetailResponse>());
        Http.OnPost("api/v1/seo/research", new SeoResearchResponse(
            [new SeoTarget("react data grid", null, null, null, "model")],
            [],
            HasProviderMetrics: false,
            Notes: ["No SEO provider is configured, so there are no volume or difficulty numbers."]));

        var view = Render<NewCampaign>();
        await EnterTargetsAsync(view);

        // A missing number renders as "—". Showing 0 would read as "nobody searches for this",
        // which is a different and false claim.
        Assert.Contains("—", view.Markup, StringComparison.Ordinal);
        Assert.Contains("no volume or difficulty numbers", view.Markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Drives the flow to the Targets step: paste a transcript, name it, ingest, then Next.
    /// </summary>
    private static async Task EnterTargetsAsync(IRenderedComponent<NewCampaign> view)
    {
        await view.InvokeAsync(() =>
        {
            var step = typeof(NewCampaign).GetField("_step",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var campaign = typeof(NewCampaign).GetField("_campaignId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var transcript = typeof(NewCampaign).GetField("_transcriptArtifactId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            campaign.SetValue(view.Instance, CampaignId);
            transcript.SetValue(view.Instance, TranscriptId);
            step.SetValue(view.Instance, Enum.Parse(step.FieldType, "Brief"));
        });

        view.Render();

        await view.InvokeAsync(async () =>
        {
            var go = typeof(NewCampaign).GetMethod("GoToTargetsAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            await (Task)go.Invoke(view.Instance, null)!;
        });

        view.Render();
    }
}
