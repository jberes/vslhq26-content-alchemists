using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Pages;

namespace Castmill.UI.Tests;

/// <summary>
/// The expanded brand editor (ADR-062): market and messaging on Identity, usage rules and
/// gradients on Colours. What matters is the round trip — a card loads into the fields and
/// saves back without losing a competitor, a persona or a gradient.
/// </summary>
public sealed class BrandMarketFieldsTests : CastmillUiTestContext
{
    private static readonly Guid BrandId = Guid.Parse("e7777777-1111-1111-1111-111111111111");

    private static BrandStyleCard Card() => new(
        Voice: "Technical, concise.",
        Colors: [new BrandColor("cta", "#2B46BB", "--color-primary-action")],
        Positioning: "Reveal is an embedded analytics platform for software teams.",
        MessagingPillars: ["Embedded", "Control"],
        Differentiators: ["Embedded-first orientation"],
        ProofPoints: ["Faster time to market"],
        Competitors: [new BrandCompetitor("Power BI Embedded", "Azure-centric teams")],
        Personas: [new BrandPersona("Embedded Analytics PM", "Owns analytics in a SaaS product",
            Goals: ["Launch faster"], PainPoints: ["Builds take too long"], DecisionCriteria: ["Ease of embedding"])],
        UseCases: ["SaaS product analytics"],
        DoNotClaim: ["Do not conflate Reveal with Ignite UI"],
        ColorUsage: "70-85% neutral. CTA must be blue.",
        Gradients: [new BrandGradient("primary", "linear-gradient(90deg,#FBB764 0%,#EC417A 40%)")],
        LayoutPattern: "Hero -> Features -> CTA",
        VisualDontList: ["overuse pink"],
        QaChecklist: ["Enterprise SaaS feel"],
        MarketingMode: "More contrast.",
        ProductMode: "Neutral, white-label ready.");

    public BrandMarketFieldsTests()
    {
        SignInTestUser();
        Http.OnGet($"api/v1/brands/{BrandId}",
            new BrandProfileDetailResponse(BrandId, "Reveal", Card(), null, DateTimeOffset.UtcNow));
        Http.OnGet("api/v1/brands", new List<BrandProfileDetailResponse>());
        Http.OnGet($"api/v1/brands/{BrandId}/assets", new List<BrandAssetResponse>());
        Http.OnGet($"api/v1/brands/{BrandId}/templates", new List<BrandTemplateResponse>());
        Http.OnPut($"api/v1/brands/{BrandId}",
            new BrandProfileDetailResponse(BrandId, "Reveal", Card(), null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Market_and_messaging_load_into_the_identity_tab()
    {
        var view = await OpenAsync();

        var text = view.Markup;
        Assert.Contains("Market &amp; messaging", text, StringComparison.Ordinal);
        Assert.Contains("Reveal is an embedded analytics platform", text, StringComparison.Ordinal);
        Assert.Contains("Power BI Embedded", text, StringComparison.Ordinal);
        Assert.Contains("Embedded Analytics PM", text, StringComparison.Ordinal);
        Assert.Contains("Do not conflate Reveal with Ignite UI", text, StringComparison.Ordinal);
        // A persona is a card of its own, not another row.
        Assert.Single(view.FindAll(".cm-brand__persona"));
    }

    [Fact]
    public async Task Saving_round_trips_every_new_field()
    {
        var view = await OpenAsync();

        await view.FindAll("button.cm-button").Single(b => b.TextContent.Trim() == "Save brand").ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var body = Http.Bodies.Single(b => b.Method == HttpMethod.Put
                && b.Path.EndsWith($"brands/{BrandId}", StringComparison.Ordinal)).Body;
            Assert.Contains("\"positioning\":\"Reveal is an embedded analytics platform for software teams.\"", body, StringComparison.Ordinal);
            Assert.Contains("\"messagingPillars\":[\"Embedded\",\"Control\"]", body, StringComparison.Ordinal);
            Assert.Contains("\"name\":\"Power BI Embedded\"", body, StringComparison.Ordinal);
            Assert.Contains("\"whenItComesUp\":\"Azure-centric teams\"", body, StringComparison.Ordinal);
            Assert.Contains("\"title\":\"Embedded Analytics PM\"", body, StringComparison.Ordinal);
            Assert.Contains("\"goals\":[\"Launch faster\"]", body, StringComparison.Ordinal);
            Assert.Contains("\"doNotClaim\":[\"Do not conflate Reveal with Ignite UI\"]", body, StringComparison.Ordinal);
            Assert.Contains("linear-gradient(90deg,#FBB764 0%,#EC417A 40%)", body, StringComparison.Ordinal);
            Assert.Contains("\"token\":\"--color-primary-action\"", body, StringComparison.Ordinal);
            Assert.Contains("\"qaChecklist\":[\"Enterprise SaaS feel\"]", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task An_empty_list_saves_as_absent_rather_than_an_empty_array()
    {
        Http.OnGet($"api/v1/brands/{BrandId}",
            new BrandProfileDetailResponse(BrandId, "Bare", new BrandStyleCard(Voice: "Plain."), null, DateTimeOffset.UtcNow));

        var view = await OpenAsync();
        await view.FindAll("button.cm-button").Single(b => b.TextContent.Trim() == "Save brand").ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var body = Http.Bodies.Single(b => b.Method == HttpMethod.Put
                && b.Path.EndsWith($"brands/{BrandId}", StringComparison.Ordinal)).Body;
            Assert.Contains("\"positioning\":null", body, StringComparison.Ordinal);
            Assert.Contains("\"messagingPillars\":null", body, StringComparison.Ordinal);
            Assert.Contains("\"competitors\":null", body, StringComparison.Ordinal);
        });
    }

    private async Task<IRenderedComponent<BrandEditor>> OpenAsync()
    {
        var view = Render<BrandEditor>(p => p.Add(page => page.BrandId, BrandId));
        await view.WaitForStateAsync(
            () => view.FindAll("[role=tab]").Count == 6, TimeSpan.FromSeconds(5));
        return view;
    }
}
