using System.Collections;
using System.Reflection;
using System.Text.Json;
using Bunit;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages;
using Castmill.UI.State;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

public sealed class N27StartRunTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId =
        Guid.Parse("92700000-0000-0000-0000-000000000001");
    private static readonly Guid SourceId =
        Guid.Parse("92700000-0000-0000-0000-000000000002");
    private static readonly Guid ReportId =
        Guid.Parse("92700000-0000-0000-0000-000000000003");
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Source_step_shows_only_capabilities_working_in_the_current_shell()
    {
        ArrangeBase();
        var web = Render<NewCampaign>();

        var webStarters = web.FindAll(".cm-starter").Select(item => item.TextContent).ToList();
        Assert.Equal(5, webStarters.Count);
        Assert.Contains(webStarters, text => text.Contains("Paste text", StringComparison.Ordinal));
        Assert.Contains(webStarters, text => text.Contains("Import webpage", StringComparison.Ordinal));
        Assert.Contains(webStarters, text => text.Contains("Upload document", StringComparison.Ordinal));
        Assert.Contains(webStarters, text => text.Contains("Upload media", StringComparison.Ordinal));
        Assert.Contains(webStarters, text => text.Contains("Record an idea", StringComparison.Ordinal));
        Assert.DoesNotContain(webStarters, text => text.Contains("Local media", StringComparison.Ordinal));
        Assert.DoesNotContain("Not available", web.Markup, StringComparison.Ordinal);

        Media.EnableLocalProcessing();
        var desktop = Render<NewCampaign>();
        Assert.Contains(desktop.FindAll(".cm-starter"), item =>
            item.TextContent.Contains("Local media", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Webpage_starter_saves_source_then_opens_the_separate_intent_step()
    {
        ArrangeBase();
        Http.OnPost("api/v1/campaigns", Campaign());
        Http.OnPost(
            $"api/v1/campaigns/{CampaignId}/sources/import/webpage",
            Evidence());
        var draft = DraftExcludedEvidence();
        Http.OnPatch(
            $"api/v1/campaigns/{CampaignId}/sources/{SourceId}/evidence/web-0001",
            draft);
        Http.OnPost(
            $"api/v1/campaigns/{CampaignId}/sources/{SourceId}/evidence/2/approve",
            draft with { IsApproved = true });
        var view = Render<NewCampaign>();

        view.FindAll(".cm-starter").Single(item =>
            item.TextContent.Contains("Import webpage", StringComparison.Ordinal)).Click();
        view.Find("input[placeholder='Q3 product webinar']").Input("Launch source");
        view.Find("input[placeholder='https://example.com/article']")
            .Input("https://example.com/launch");
        view.FindAll("button").Single(button =>
            button.TextContent.Trim() == "Import webpage").Click();

        view.WaitForAssertion(() =>
            Assert.Contains("Choose the campaign intent", view.Markup, StringComparison.Ordinal));
        Assert.Contains(Http.Bodies, request =>
            request.Method == HttpMethod.Post
            && request.Path.EndsWith("/sources/import/webpage", StringComparison.Ordinal)
            && request.Body.Contains("https://example.com/launch", StringComparison.Ordinal));
        Assert.Empty(view.FindAll(".cm-evidence-review"));
        Assert.Contains("Source ready", view.Find(".cm-run__source-summary").TextContent,
            StringComparison.Ordinal);
        var intents = view.FindAll(".cm-intent");
        Assert.Equal(2, intents.Count);
        Assert.Contains(intents, item =>
            item.TextContent.Contains("Repurpose this page", StringComparison.Ordinal));
        Assert.Contains(intents, item =>
            item.TextContent.Contains("Promote or expand this page", StringComparison.Ordinal));

        view.FindAll("button").Single(button => button.TextContent.Trim() == "Review source").Click();
        Assert.Contains("Source evidence", view.Find(".cm-evidence-review").GetAttribute("aria-label"));
        view.FindAll("button").Single(button => button.TextContent.Trim() == "Exclude").Click();
        view.WaitForAssertion(() => Assert.Contains("Draft r2", view.Markup, StringComparison.Ordinal));
        Assert.All(view.FindAll(".cm-intent"), intent => Assert.True(intent.HasAttribute("disabled")));
        Assert.Contains(Http.Bodies, request =>
            request.Method == HttpMethod.Patch
            && request.Path.EndsWith("/evidence/web-0001", StringComparison.Ordinal)
            && request.Body.Contains("\"isExcluded\":true", StringComparison.Ordinal));

        view.FindAll("button").Single(button =>
            button.TextContent.Contains("Approve revision", StringComparison.Ordinal)).Click();
        view.WaitForAssertion(() => Assert.DoesNotContain("Draft r2", view.Markup, StringComparison.Ordinal));
        Assert.All(view.FindAll(".cm-intent"), intent => Assert.False(intent.HasAttribute("disabled")));
        view.FindAll("button").Single(button => button.TextContent.Trim() == "Close").Click();
        Assert.Empty(view.FindAll(".cm-evidence-review"));
    }

    [Fact]
    public async Task Intent_selection_is_persisted_before_context_and_audience_inference()
    {
        ArrangeBase();
        Http.OnPut($"api/v1/campaigns/{CampaignId}", Campaign() with
        {
            Intent = CampaignIntent.Launch,
        });
        Http.OnPost(
            $"api/v1/ai/campaigns/{CampaignId}/research-context",
            new ResearchContextSuggestionResponse("Platform leaders launching governed analytics"));
        var view = Render<NewCampaign>();
        SetRunState(view, "Intent");

        view.FindAll(".cm-intent").Single(item =>
            item.TextContent.Contains("Launch", StringComparison.Ordinal)).Click();

        view.WaitForAssertion(() =>
            Assert.Contains("Set the research context", view.Markup, StringComparison.Ordinal));
        var save = Http.Bodies.Last(request =>
            request.Method == HttpMethod.Put && request.Path.EndsWith(CampaignId.ToString(), StringComparison.Ordinal));
        Assert.Contains($"\"intent\":\"{CampaignIntent.Launch}\"", save.Body, StringComparison.Ordinal);
        Assert.Contains("\"outputRecipe\":[\"youtube\",\"blog\"]", save.Body, StringComparison.Ordinal);
        Assert.Contains("Platform leaders launching governed analytics", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reload_restores_intent_approved_analysis_and_output_recipe()
    {
        ArrangeResume();
        var view = Render<NewCampaign>();
        await InvokePrivateAsync(view, "ResumeAsync", CampaignId);
        view.Render();

        Assert.Contains("Choose the output recipe", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Launch", view.Find(".cm-seo__banner").TextContent, StringComparison.Ordinal);
        var choices = view.FindAll(".cm-fanout__item").ToDictionary(
            item => item.TextContent.Trim(),
            item => item.QuerySelector("input")!.HasAttribute("checked"));
        Assert.True(choices["Newsletter"]);
        Assert.False(choices["YouTube package"]);
        Assert.False(choices["Blog post"]);
    }

    [Fact]
    public async Task Saved_recipe_reaches_press_run_only_after_approved_analysis()
    {
        ArrangeResume();
        Http.OnPut($"api/v1/campaigns/{CampaignId}", Campaign() with
        {
            Intent = CampaignIntent.Launch,
            OutputRecipe = ["newsletter"],
        });
        Http.OnPut(
            $"api/v1/campaigns/{CampaignId}/seo-targets",
            Targets());
        Http.OnPost(
            $"api/v1/ai/campaigns/{CampaignId}/generate",
            new RunFinished(Guid.NewGuid(), 1, 0, []));
        var view = Render<NewCampaign>();
        await InvokePrivateAsync(view, "ResumeAsync", CampaignId);
        view.Render();

        await InvokePrivateAsync(view, "PressRunAsync");
        view.WaitForAssertion(() => Assert.Contains(Http.Bodies, request =>
            request.Method == HttpMethod.Post && request.Path.EndsWith("/generate", StringComparison.Ordinal)));
        var generate = Http.Bodies.Last(request => request.Path.EndsWith("/generate", StringComparison.Ordinal));
        Assert.Contains("\"transcriptArtifactId\":null", generate.Body, StringComparison.Ordinal);
        Assert.Contains("\"newsletter\"", generate.Body, StringComparison.Ordinal);
        Assert.Contains("\"thumbnail-concepts\"", generate.Body, StringComparison.Ordinal);
        Assert.Contains("\"image-prompts\"", generate.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"youtube\"", generate.Body, StringComparison.Ordinal);

        var press = Services.GetRequiredService<PressRunService>();
        Assert.Equal(CampaignId, press.CampaignId);
        Assert.Contains("newsletter", press.Kinds);
    }

    [Fact]
    public async Task Skip_seo_requires_no_url_and_goes_directly_to_content_generation()
    {
        ArrangeBase();
        Http.OnPut($"api/v1/campaigns/{CampaignId}", Campaign() with
        {
            Intent = CampaignIntent.Launch,
            SkipSeoAnalysis = true,
        });
        Http.OnPut($"api/v1/campaigns/{CampaignId}/seo-targets",
            new SeoTargetsResponse(null, [], []));
        Http.OnPost($"api/v1/ai/campaigns/{CampaignId}/brief",
            new BriefSuggestionResponse(
                "Source-led title", "Platform leaders", "Direct", "Lead with proof",
                "A source-informed brief.", ["Use the measured result."]));
        Http.OnPost($"api/v1/ai/campaigns/{CampaignId}/generate",
            new RunFinished(Guid.NewGuid(), 2, 0, []));
        var view = Render<NewCampaign>();
        SetRunState(view, "Context");
        SetPrivateField(view, "_intent", CampaignIntent.Launch);
        SetPrivateField(view, "_audience", "Platform leaders");
        SetPrivateField(view, "_siteUrl", string.Empty);
        view.Render();

        Assert.False(view.FindAll("button").Single(button =>
            button.TextContent.Contains("Create content without SEO", StringComparison.Ordinal))
            .HasAttribute("disabled"));
        Assert.True(view.FindAll("button").Single(button =>
            button.TextContent.Contains("Build the deep SEO/AEO report", StringComparison.Ordinal))
            .HasAttribute("disabled"));

        await InvokePrivateAsync(view, "SkipSeoAsync");
        view.Render();

        Assert.Contains("Choose the output recipe", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Built from approved source evidence", view.Markup, StringComparison.Ordinal);
        Assert.Contains("SEO/AEO skipped", view.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(Http.Requests, request =>
            request.RequestUri!.AbsolutePath.EndsWith("/deep-analysis", StringComparison.Ordinal));
        var campaignSave = Http.Bodies.Last(body =>
            body.Method == HttpMethod.Put && body.Path.EndsWith(CampaignId.ToString(), StringComparison.Ordinal));
        Assert.Contains("\"skipSeoAnalysis\":true", campaignSave.Body, StringComparison.Ordinal);
        Assert.Contains(Http.Bodies, body =>
            body.Method == HttpMethod.Put
            && body.Path.EndsWith("/seo-targets", StringComparison.Ordinal)
            && body.Body.Contains("\"keywords\":[]", StringComparison.Ordinal));

        var targetSavesBeforeRun = Http.Bodies.Count(body => body.Path.EndsWith("/seo-targets", StringComparison.Ordinal));
        await InvokePrivateAsync(view, "PressRunAsync");
        await view.WaitForStateAsync(() => Http.Bodies.Any(body =>
            body.Method == HttpMethod.Post && body.Path.EndsWith("/generate", StringComparison.Ordinal)));
        Assert.Equal(targetSavesBeforeRun,
            Http.Bodies.Count(body => body.Path.EndsWith("/seo-targets", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Reload_restores_the_skipped_seo_path_at_output_recipe()
    {
        ArrangeBase();
        Http.OnGet($"api/v1/campaigns/{CampaignId}", Campaign() with
        {
            Brief = "Campaign intent: launch\nSEO/AEO: Skipped\nAudience: Platform leaders\nAngle: Lead with proof",
            Intent = CampaignIntent.Launch,
            OutputRecipe = ["blog"],
            SkipSeoAnalysis = true,
        });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(), [], [], 0, 0, Sources: [Source()]));
        var view = Render<NewCampaign>();

        await InvokePrivateAsync(view, "ResumeAsync", CampaignId);
        view.Render();

        Assert.Contains("Choose the output recipe", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Built from approved source evidence", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Back to context", view.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(Http.Requests, request =>
            request.RequestUri!.AbsolutePath.Contains("/seo/reports/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rapid_recipe_edits_are_serialized_and_the_latest_selection_wins()
    {
        ArrangeResume();
        var firstSaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var putCalls = 0;
        var activeCalls = 0;
        var maxActiveCalls = 0;
        Http.OnAsync(HttpMethod.Put, $"api/v1/campaigns/{CampaignId}", async () =>
        {
            var call = Interlocked.Increment(ref putCalls);
            var active = Interlocked.Increment(ref activeCalls);
            maxActiveCalls = Math.Max(maxActiveCalls, active);
            try
            {
                if (call == 1)
                {
                    firstSaveStarted.SetResult();
                    await releaseFirstSave.Task;
                }
                return StubHttpHandler.Json(Campaign());
            }
            finally
            {
                Interlocked.Decrement(ref activeCalls);
            }
        });
        var view = Render<NewCampaign>();
        await InvokePrivateAsync(view, "ResumeAsync", CampaignId);
        view.Render();

        var newsletterSave = await StartRecipeChangeAsync(view, "newsletter", false);
        await firstSaveStarted.Task;
        var youtubeSave = await StartRecipeChangeAsync(view, "youtube", true);
        var blogSave = await StartRecipeChangeAsync(view, "blog", true);

        Assert.Equal(1, putCalls);
        Assert.Equal(1, maxActiveCalls);
        releaseFirstSave.SetResult();
        await Task.WhenAll(newsletterSave, youtubeSave, blogSave);

        Assert.Equal(3, putCalls);
        Assert.Equal(1, maxActiveCalls);
        var lastSave = Http.Bodies.Last(body => body.Method == HttpMethod.Put);
        Assert.Contains("\"outputRecipe\":[\"youtube\",\"blog\"]", lastSave.Body, StringComparison.Ordinal);
    }

    private void ArrangeBase()
    {
        SignInTestUser();
        Http.OnGet("api/v1/brands", new List<BrandProfileDetailResponse>());
    }

    private void ArrangeResume()
    {
        ArrangeBase();
        Http.OnGet($"api/v1/campaigns/{CampaignId}", Campaign() with
        {
            Brief = "Campaign intent: launch\nAudience: Platform leaders\nBrand voice: Direct\nAngle: Measured launch proof",
            Intent = CampaignIntent.Launch,
            OutputRecipe = ["newsletter"],
            Links = [new CampaignLink("Site", "https://example.com")],
        });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(),
            [ReportPreview()],
            [],
            0,
            0,
            Sources: [Source()]));
        Http.OnGet($"api/v1/seo/reports/{ReportId}", Analysis());
        Http.OnGet($"api/v1/campaigns/{CampaignId}/seo-targets", Targets());
    }

    private static CampaignResponse Campaign() => new(
        CampaignId,
        Guid.NewGuid(),
        "Launch source",
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        ContentType: CampaignContentType.ThoughtLeadership);

    private static SourceAssetResponse Source()
    {
        var revisionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new SourceAssetResponse(
            SourceId,
            CampaignId,
            null,
            SourceKinds.WebPage,
            SourceModalities.Web,
            "Launch page",
            "https://example.com/launch",
            "text/html",
            100,
            "sha256:source",
            1,
            revisionId,
            new ApprovedEvidenceRevision(SourceId, 1, revisionId, "approved", now),
            now,
            now);
    }

    private static EvidenceRevisionResponse Evidence()
    {
        var source = Source();
        using var locator = JsonDocument.Parse(
            """{"url":"https://example.com/launch","heading":"Launch","ordinal":1}""");
        var block = new EvidenceBlockResponse(
            source.Id,
            "web-0001",
            0,
            "The launch reduced deployment time.",
            EvidenceLocatorKinds.WebPageSection,
            locator.RootElement.Clone(),
            1,
            source.CurrentEvidenceRevisionId,
            EvidenceApprovalStates.Approved,
            false);
        return new EvidenceRevisionResponse(
            source, 1, source.CurrentEvidenceRevisionId, true, [block]);
    }

    private static EvidenceRevisionResponse DraftExcludedEvidence()
    {
        var source = Source();
        var revisionId = Guid.NewGuid();
        source = source with
        {
            CurrentEvidenceRevision = 2,
            CurrentEvidenceRevisionId = revisionId,
        };
        using var locator = JsonDocument.Parse(
            """{"url":"https://example.com/launch","heading":"Launch","ordinal":1}""");
        var block = new EvidenceBlockResponse(
            source.Id,
            "web-0001",
            0,
            "The launch reduced deployment time.",
            EvidenceLocatorKinds.WebPageSection,
            locator.RootElement.Clone(),
            2,
            revisionId,
            EvidenceApprovalStates.Draft,
            true);
        return new EvidenceRevisionResponse(source, 2, revisionId, false, [block]);
    }

    private static ArtifactPreviewResponse ReportPreview() => new(
        ReportId,
        CampaignId,
        "seo-report",
        "SEO report",
        ArtifactStatus.InReview,
        1,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private static SeoResearchResponse Research() => new(
        [new SeoTarget("deployment launch", 1000, 20, 80, "provider")],
        [new SeoQuestion("How do you launch safely?", "paa")],
        true,
        []);

    private static SeoAnalysisReportResponse Analysis() => new(
        ReportId,
        DateTimeOffset.UtcNow,
        Research(),
        new SeoSerpSnapshot("deployment launch", null, null, []),
        ["Lead with measured launch proof."]);

    private static SeoTargetsResponse Targets() => new(
        "deployment launch",
        Research().Keywords,
        Research().Questions);

    private static void SetRunState(IRenderedComponent<NewCampaign> view, string stepName)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(NewCampaign).GetField("_campaignId", flags)!.SetValue(view.Instance, CampaignId);
        typeof(NewCampaign).GetField("_name", flags)!.SetValue(view.Instance, "Launch source");
        var step = typeof(NewCampaign).GetField("_step", flags)!;
        step.SetValue(view.Instance, Enum.Parse(step.FieldType, stepName));
        view.Render();
    }

    private static void SetPrivateField(
        IRenderedComponent<NewCampaign> view, string fieldName, object? value) =>
        typeof(NewCampaign).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(view.Instance, value);

    private static async Task InvokePrivateAsync(
        IRenderedComponent<NewCampaign> view, string methodName, params object[] args)
    {
        await view.InvokeAsync(async () =>
        {
            var method = typeof(NewCampaign).GetMethod(
                methodName, BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)method.Invoke(view.Instance, args)!;
        });
    }

    private static async Task<Task> StartRecipeChangeAsync(
        IRenderedComponent<NewCampaign> view, string kind, bool selected)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var items = (IEnumerable)typeof(NewCampaign).GetField("_fanOut", flags)!.GetValue(view.Instance)!;
        var item = items.Cast<object>().Single(candidate =>
            string.Equals(
                (string)candidate.GetType().GetProperty("Kind")!.GetValue(candidate)!,
                kind,
                StringComparison.Ordinal));
        var method = typeof(NewCampaign).GetMethod("RecipeChangedAsync", flags)!;
        Task? operation = null;
        await view.InvokeAsync(() =>
        {
            operation = (Task)method.Invoke(
                view.Instance,
                [item, new ChangeEventArgs { Value = selected }])!;
        });
        return operation!;
    }
}
