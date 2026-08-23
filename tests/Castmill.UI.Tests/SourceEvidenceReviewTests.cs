using System.Text.Json;
using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Microsoft.AspNetCore.Components;

namespace Castmill.UI.Tests;

public sealed class SourceEvidenceReviewTests : CastmillUiTestContext
{
    [Fact]
    public void Review_renders_locator_origin_highlight_and_stable_deep_link()
    {
        var source = Source(SourceKinds.WebPage, SourceModalities.Web, "Product page",
            "https://example.com/product");
        var block = Block(
            source.Id,
            "web-0001",
            "The product cuts deployment time in half.",
            EvidenceLocatorKinds.WebPageSection,
            """{"url":"https://example.com/product","heading":"Measured result","element":"p","ordinal":1}""");

        var view = Render<SourceEvidenceReview>(parameters => parameters
            .Add(component => component.CampaignId, source.CampaignId)
            .Add(component => component.Source, source)
            .Add(component => component.Evidence, Revision(source, true, block))
            .Add(component => component.Highlighted,
                new HashSet<string>([block.StableId], StringComparer.OrdinalIgnoreCase)));

        Assert.Contains("Web page snapshot", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Measured result", view.Markup, StringComparison.Ordinal);
        Assert.Contains("cm-evidence-block--active", view.Markup, StringComparison.Ordinal);
        Assert.Equal("https://example.com/product",
            view.Find(".cm-evidence-review__meta a").GetAttribute("href"));
        Assert.Equal(
            $"/campaigns/{source.CampaignId}/floor?source={source.Id}&revision=1&evidence=web-0001",
            view.Find(".cm-evidence-block__link").GetAttribute("href"));
    }

    [Fact]
    public async Task Review_exposes_correction_exclusion_and_approval_actions()
    {
        var source = Source(SourceKinds.Document, SourceModalities.Document, "Brief.pdf", null);
        var block = Block(
            source.Id,
            "page-0002",
            "Original evidence.",
            EvidenceLocatorKinds.DocumentSection,
            """{"page":2}""") with { IsExcluded = false };
        (EvidenceBlockResponse Block, string Content)? correction = null;
        EvidenceBlockResponse? toggled = null;
        var approved = false;

        var view = Render<SourceEvidenceReview>(parameters => parameters
            .Add(component => component.CampaignId, source.CampaignId)
            .Add(component => component.Source, source)
            .Add(component => component.Evidence, Revision(source, false, block))
            .Add(component => component.OnCorrect,
                EventCallback.Factory.Create<(EvidenceBlockResponse Block, string Content)>(
                    this, value => correction = value))
            .Add(component => component.OnToggleExcluded,
                EventCallback.Factory.Create<EvidenceBlockResponse>(this, value => toggled = value))
            .Add(component => component.OnApprove,
                EventCallback.Factory.Create(this, () => approved = true)));

        view.FindAll("button").Single(button => button.TextContent.Contains("Correct", StringComparison.Ordinal)).Click();
        view.Find("textarea").Change("Corrected evidence.");
        view.FindAll("button").Single(button => button.TextContent.Contains("Save correction", StringComparison.Ordinal)).Click();
        await view.InvokeAsync(() => Task.CompletedTask);
        Assert.Equal("Corrected evidence.", correction?.Content);

        view.FindAll("button").Single(button => button.TextContent.Contains("Exclude", StringComparison.Ordinal)).Click();
        await view.InvokeAsync(() => Task.CompletedTask);
        Assert.Equal(block.StableId, toggled?.StableId);

        view.FindAll("button").Single(button => button.TextContent.Contains("Approve revision", StringComparison.Ordinal)).Click();
        await view.InvokeAsync(() => Task.CompletedTask);
        Assert.True(approved);
    }

    [Fact]
    public void Web_review_labels_metadata_and_exposes_eligible_image_without_hotlinking_it()
    {
        var source = Source(SourceKinds.WebPage, SourceModalities.Web, "Product page",
            "https://example.com/product");
        var metadata = Block(
            source.Id,
            "metadata-author",
            "Author: Ada Lovelace",
            EvidenceLocatorKinds.WebPageMetadata,
            """{"url":"https://example.com/product","field":"author","label":"Author"}""");
        var image = Block(
            source.Id,
            "image-0001",
            "Eligible image: Product dashboard",
            EvidenceLocatorKinds.WebPageImage,
            """{"url":"https://example.com/images/dashboard.webp","alt":"Product dashboard","width":1200,"height":630}""");

        var view = Render<SourceEvidenceReview>(parameters => parameters
            .Add(component => component.CampaignId, source.CampaignId)
            .Add(component => component.Source, source)
            .Add(component => component.Evidence, Revision(source, true, metadata, image)));

        Assert.Contains("Author", view.FindAll(".cm-evidence-block__head")[0].TextContent,
            StringComparison.Ordinal);
        var imageLink = view.FindAll("a").Single(link =>
            link.TextContent.Contains("Open eligible image", StringComparison.Ordinal));
        Assert.Equal("https://example.com/images/dashboard.webp", imageLink.GetAttribute("href"));
        Assert.Empty(view.FindAll("img"));
    }

    internal static SourceAssetResponse Source(
        string kind, string modality, string label, string? originalUri)
    {
        var id = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new SourceAssetResponse(
            id,
            Guid.NewGuid(),
            null,
            kind,
            modality,
            label,
            originalUri,
            "text/plain",
            200,
            "sha256:snapshot",
            1,
            revisionId,
            new ApprovedEvidenceRevision(id, 1, revisionId, "approved", now),
            now,
            now);
    }

    internal static EvidenceBlockResponse Block(
        Guid sourceId, string stableId, string content, string locatorKind, string locatorJson)
    {
        using var locator = JsonDocument.Parse(locatorJson);
        return new EvidenceBlockResponse(
            sourceId,
            stableId,
            0,
            content,
            locatorKind,
            locator.RootElement.Clone(),
            1,
            Guid.NewGuid(),
            EvidenceApprovalStates.Approved,
            false);
    }

    internal static EvidenceRevisionResponse Revision(
        SourceAssetResponse source, bool approved, params EvidenceBlockResponse[] blocks) =>
        new(source, 1, source.CurrentEvidenceRevisionId, approved, blocks);
}
