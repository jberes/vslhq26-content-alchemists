using Bunit;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Castmill.UI.Pages;

namespace Castmill.UI.Tests;

public sealed class ResearchContextStepTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
    private static readonly Guid TranscriptId = Guid.Parse("bbbbbbbb-1111-2222-3333-cccccccccccc");
    private static readonly Guid BrandId = Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd");

    [Fact]
    public async Task Audience_is_ai_generated_and_voice_is_inherited_from_the_selected_brand()
    {
        SignInTestUser();
        var voice = "Precise, candid, technically rigorous, and free of hype";
        Http.OnGet("api/v1/brands", new List<BrandProfileDetailResponse>
        {
            new(BrandId, "Acme", new BrandStyleCard(Voice: voice), null, DateTimeOffset.UtcNow),
        });
        Http.OnPost($"api/v1/ai/campaigns/{CampaignId}/research-context",
            new ResearchContextSuggestionResponse(
                "Platform engineers evaluating governed embedded analytics"));

        var view = Render<NewCampaign>();
        await ShowContextAsync(view);

        await view.InvokeAsync(async () =>
        {
            var suggest = typeof(NewCampaign).GetMethod("SuggestResearchContextAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            await (Task)suggest.Invoke(view.Instance, null)!;
        });
        view.Render();

        Assert.Contains("Platform engineers evaluating governed embedded analytics", view.Markup,
            StringComparison.Ordinal);
        Assert.Contains(Http.Requests, request =>
            request.Method == HttpMethod.Post
            && request.RequestUri!.AbsolutePath.EndsWith("/research-context", StringComparison.Ordinal));

        view.Find("select.cm-brand__kind").Change(BrandId.ToString());

        var voiceField = view.FindAll("textarea")
            .Single(field => field.ParentElement?.TextContent.Contains(
                "Brand voice — from selected Brand", StringComparison.Ordinal) == true);
        Assert.True(voiceField.HasAttribute("readonly"));
        Assert.Equal(voice, voiceField.GetAttribute("value"));
    }

    [Fact]
    public async Task Selecting_none_clears_the_inherited_voice()
    {
        SignInTestUser();
        Http.OnGet("api/v1/brands", new List<BrandProfileDetailResponse>
        {
            new(BrandId, "Acme", new BrandStyleCard(Voice: "Acme voice"), null, DateTimeOffset.UtcNow),
        });

        var view = Render<NewCampaign>();
        await ShowContextAsync(view);
        var picker = view.Find("select.cm-brand__kind");
        picker.Change(BrandId.ToString());
        picker.Change(string.Empty);

        var voiceField = view.FindAll("textarea")
            .Single(field => field.ParentElement?.TextContent.Contains(
                "Brand voice — from selected Brand", StringComparison.Ordinal) == true);
        Assert.Equal(string.Empty, voiceField.GetAttribute("value"));
        Assert.Contains("Select a Brand to inherit its voice", view.Markup, StringComparison.Ordinal);
    }

    private static async Task ShowContextAsync(IRenderedComponent<NewCampaign> view)
    {
        await view.InvokeAsync(() =>
        {
            var flags = System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance;
            typeof(NewCampaign).GetField("_campaignId", flags)!.SetValue(view.Instance, CampaignId);
            typeof(NewCampaign).GetField("_transcriptArtifactId", flags)!.SetValue(view.Instance, TranscriptId);
            var step = typeof(NewCampaign).GetField("_step", flags)!;
            step.SetValue(view.Instance, Enum.Parse(step.FieldType, "Context"));
        });
        view.Render();
    }
}
