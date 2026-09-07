using Castmill.Api.Services.Ai;
using Castmill.Core.Resources;

namespace Castmill.Api.Tests;

/// <summary>
/// One campaign, one reader (ADR-068). A brand can hold a developer, an architect and a CTO;
/// a piece written to the average of all three lands with none of them, so a campaign names
/// which one it is for and the others leave the prompt.
/// </summary>
public sealed class CampaignAudienceTests
{
    private static readonly BrandStyleCard Card = new(
        Personas:
        [
            new BrandPersona("Developer", "Builds the screens",
                Goals: ["Ship the grid this sprint"], PainPoints: ["Docs assume too much"]),
            new BrandPersona("Architect", "Chooses the stack",
                Goals: ["Pick something that lasts"], PainPoints: ["Migration cost"]),
            new BrandPersona("CTO", "Approves the spend", Goals: ["Reduce roadmap risk"]),
        ]);

    [Fact]
    public void Naming_an_audience_keeps_that_persona_and_drops_the_others()
    {
        var block = Block(Card, "Architect");

        Assert.Contains("Write this campaign for this reader, and no other:", block, StringComparison.Ordinal);
        Assert.Contains("Architect", block, StringComparison.Ordinal);
        Assert.Contains("Pick something that lasts", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Ship the grid this sprint", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Reduce roadmap risk", block, StringComparison.Ordinal);
    }

    [Fact]
    public void No_audience_keeps_every_persona_as_before()
    {
        var block = Block(Card, null);

        Assert.Contains("Who this is written for:", block, StringComparison.Ordinal);
        Assert.Contains("Ship the grid this sprint", block, StringComparison.Ordinal);
        Assert.Contains("Pick something that lasts", block, StringComparison.Ordinal);
    }

    [Fact]
    public void An_audience_the_brand_does_not_list_is_still_honoured()
    {
        // Free text on purpose: a campaign is never blocked by the brand's list.
        var block = Block(Card, "Procurement lead");

        Assert.Contains("Write this campaign for: Procurement lead.", block, StringComparison.Ordinal);
        // With no matching persona the brand's own list still gives the writer something.
        Assert.Contains("Who this is written for:", block, StringComparison.Ordinal);
    }

    private static string Block(BrandStyleCard card, string? audience) =>
        (string)typeof(BrandContextService)
            .GetMethod("BuildStyleBlock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, ["Ignite UI", card, audience])!;
}
