using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Design;

namespace Castmill.UI.Tests;

public sealed class ContentImpactReviewTests : CastmillUiTestContext
{
    [Fact]
    public async Task Review_lists_only_affected_content_and_requires_an_explicit_action()
    {
        var evidence = new ApprovedEvidenceRevision(
            Guid.NewGuid(), 2, Guid.NewGuid(), "hash", DateTimeOffset.UnixEpoch);
        var identity = new ContentDependencyIdentity(
            [evidence], Guid.NewGuid(), 2, "report", "targets");
        var staleId = Guid.NewGuid();
        var legacyId = Guid.NewGuid();
        var kept = Guid.Empty;
        var regenerated = Guid.Empty;
        var review = new ContentImpactReviewResponse(Guid.NewGuid(),
        [
            new ContentImpactItemResponse(
                Guid.NewGuid(), "blog", "Current blog", ContentStalenessStates.Fresh,
                [], identity, identity, true, true, null),
            new ContentImpactItemResponse(
                staleId, "newsletter", "Launch newsletter", ContentStalenessStates.BothChanged,
                [new ContentImpactReason("evidence", "Approved evidence changed."),
                 new ContentImpactReason("strategy", "Approved strategy changed.")],
                identity, identity, true, true, null),
            new ContentImpactItemResponse(
                legacyId, "blog", "Legacy blog", ContentStalenessStates.Unknown,
                [new ContentImpactReason("transition", "Needs transition review.")],
                null, new ContentDependencyIdentity([], null, null, null, null),
                false, false, "Approve evidence and strategy first."),
        ]);

        var view = Render<ContentImpactReview>(parameters => parameters
            .Add(component => component.Review, review)
            .Add(component => component.OnKeep,
                id => kept = id)
            .Add(component => component.OnRegenerate,
                id => regenerated = id));

        Assert.DoesNotContain("Current blog", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Launch newsletter", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Evidence + strategy changed", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Needs transition", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Nothing changes until you choose", view.Markup, StringComparison.Ordinal);

        var staleRow = view.FindAll("tr").Single(row => row.TextContent.Contains("Launch newsletter"));
        await staleRow.GetElementsByTagName("button")[0].ClickAsync();
        Assert.Equal(staleId, kept);
        staleRow = view.FindAll("tr").Single(row => row.TextContent.Contains("Launch newsletter"));
        await staleRow.GetElementsByTagName("button")[1].ClickAsync();
        Assert.Equal(staleId, regenerated);

        var legacyRow = view.FindAll("tr").Single(row => row.TextContent.Contains("Legacy blog"));
        Assert.All(legacyRow.GetElementsByTagName("button"), button => Assert.True(button.HasAttribute("disabled")));
    }
}