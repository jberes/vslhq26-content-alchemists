using System.Text;
using Castmill.Api.Services.Ai;
using Castmill.Core.Resources;

namespace Castmill.Api.Tests;

/// <summary>
/// The expanded style card (ADR-062). A brand used to carry eight fields, so a pasted
/// positioning doc lost its competitors, personas, differentiators, guardrails and colour
/// rules on import. These pin the two things that decide whether the extra fields are worth
/// storing: colours arrive as distinct rows, and the fields actually reach the prompt.
/// </summary>
public sealed class BrandColorRoleTests
{
    [Fact]
    public void Guide_wording_maps_onto_known_roles()
    {
        Assert.Equal("cta", BrandColorRoles.Normalize("Primary Blue (CTA)"));
        Assert.Equal("cta", BrandColorRoles.Normalize("primary action"));
        Assert.Equal("background", BrandColorRoles.Normalize("Secondary Background"));
        Assert.Equal("border", BrandColorRoles.Normalize("Border Gray"));
        Assert.Equal("text", BrandColorRoles.Normalize("Inverse Text"));
        Assert.Equal("neutral", BrandColorRoles.Normalize("Light Gray"));
        Assert.Equal("primary", BrandColorRoles.Normalize("Primary"));
        Assert.Equal("accent", BrandColorRoles.Normalize("accent pink"));
        // A brand's own vocabulary is kept, not overwritten.
        Assert.Equal("chart series 3", BrandColorRoles.Normalize("chart series 3"));
        Assert.Equal("accent", BrandColorRoles.Normalize("   "));
    }

    [Fact]
    public void The_same_hex_twice_is_one_colour_and_repeated_roles_are_numbered()
    {
        // The shape the Reveal guide imported as: three "accent" rows, one duplicated pink.
        var deduped = BrandColorRoles.Dedupe(
        [
            new BrandColor("accent pink", "#EC417A"),
            new BrandColor("accent amber", "#FBB764"),
            new BrandColor("Magenta", "#ec417a"),
            new BrandColor("Primary Blue (CTA)", "#2B46BB"),
        ]);

        Assert.Equal(3, deduped.Count);
        Assert.Equal(["accent", "accent 2", "cta"], deduped.Select(c => c.Role).ToArray());
        Assert.Equal(["#EC417A", "#FBB764", "#2B46BB"], deduped.Select(c => c.Hex).ToArray());
    }
}

public sealed class BrandStyleBlockTests
{
    [Fact]
    public void The_written_block_carries_the_market_fields_and_ends_on_the_guardrail()
    {
        var card = new BrandStyleCard(
            Voice: "Technical, concise.",
            Positioning: "Reveal is an embedded analytics platform for software teams.",
            MessagingPillars: ["Embedded", "Control"],
            Differentiators: ["Embedded-first orientation"],
            ProofPoints: ["Faster time to market than custom development"],
            Competitors: [new BrandCompetitor("Power BI Embedded", "Azure-centric teams")],
            Personas: [new BrandPersona("Embedded Analytics PM", "Owns analytics in a SaaS product",
                Goals: ["Launch faster"], PainPoints: ["Internal builds take too long"],
                DecisionCriteria: ["Ease of embedding"])],
            UseCases: ["SaaS product analytics"],
            DoNotClaim: ["Do not conflate Reveal with Ignite UI, App Builder or Slingshot"]);

        var block = Block(card);

        Assert.Contains("Positioning: Reveal is an embedded analytics platform", block, StringComparison.Ordinal);
        Assert.Contains("Embedded; Control", block, StringComparison.Ordinal);
        Assert.Contains("Embedded-first orientation", block, StringComparison.Ordinal);
        Assert.Contains("Faster time to market", block, StringComparison.Ordinal);
        Assert.Contains("Power BI Embedded — Azure-centric teams", block, StringComparison.Ordinal);
        Assert.Contains("Embedded Analytics PM", block, StringComparison.Ordinal);
        Assert.Contains("wants: Launch faster", block, StringComparison.Ordinal);
        Assert.Contains("SaaS product analytics", block, StringComparison.Ordinal);

        // The guardrail is last: nearest instruction to the work.
        Assert.Contains("MUST NOT", block, StringComparison.Ordinal);
        Assert.EndsWith("Do not conflate Reveal with Ignite UI, App Builder or Slingshot", block, StringComparison.Ordinal);
    }

    [Fact]
    public void A_card_with_only_the_old_fields_reads_exactly_as_before()
    {
        var block = Block(new BrandStyleCard(Voice: "Technical.", Audience: "Engineers"));

        Assert.Contains("Brand voice: Technical.", block, StringComparison.Ordinal);
        Assert.DoesNotContain("MUST NOT", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Positioning", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Competitors", block, StringComparison.Ordinal);
    }

    [Fact]
    public void The_image_block_carries_the_usage_rules_gradients_and_the_never_list()
    {
        var card = new BrandStyleCard(
            ImageStyle: "Clean enterprise SaaS imagery.",
            Colors: [new BrandColor("cta", "#2B46BB")],
            ColorUsage: "70-85% neutral. Primary CTA must be blue.",
            Gradients: [new BrandGradient("primary", "linear-gradient(90deg,#FBB764 0%,#EC417A 40%)")],
            LayoutPattern: "Hero -> Features -> CTA",
            VisualDontList: ["overuse pink", "heavy gradients"]);

        var block = ImageBlock(card);

        Assert.Contains("Colour usage: 70-85% neutral", block, StringComparison.Ordinal);
        Assert.Contains("linear-gradient(90deg,#FBB764 0%,#EC417A 40%)", block, StringComparison.Ordinal);
        Assert.Contains("Layout convention: Hero -> Features -> CTA", block, StringComparison.Ordinal);
        Assert.Contains("Avoid: overuse pink; heavy gradients", block, StringComparison.Ordinal);
    }

    private static string Block(BrandStyleCard card) =>
        (string)typeof(BrandContextService)
            .GetMethod("BuildStyleBlock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, ["Reveal", card, null])!;

    private static string ImageBlock(BrandStyleCard card) =>
        (string)typeof(BrandContextService)
            .GetMethod("BuildImageStyleBlock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [card, Array.Empty<(string, string)>()])!;
}
