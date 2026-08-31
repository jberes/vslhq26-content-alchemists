using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;
using Castmill.UI.State;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

public sealed class MillFloorSourceEvidenceTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId =
        Guid.Parse("81111111-1111-1111-1111-111111111111");

    public MillFloorSourceEvidenceTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse>
        {
            Campaign(),
        });
    }

    [Fact]
    public async Task Qualified_citation_selects_and_highlights_the_non_transcript_source()
    {
        var web = SourceEvidenceReviewTests.Source(
            SourceKinds.WebPage, SourceModalities.Web, "Launch page", "https://example.com/launch")
            with { CampaignId = CampaignId };
        var document = SourceEvidenceReviewTests.Source(
            SourceKinds.Document, SourceModalities.Document, "Proof.pdf", null)
            with { CampaignId = CampaignId };
        var webBlock = SourceEvidenceReviewTests.Block(
            web.Id, "web-0001", "Launch page evidence.",
            EvidenceLocatorKinds.WebPageSection,
            """{"heading":"Launch","ordinal":1}""");
        var documentBlock = SourceEvidenceReviewTests.Block(
            document.Id, "page-0003", "Measured proof from page three.",
            EvidenceLocatorKinds.DocumentSection,
            """{"page":3}""");
        var artifact = Artifact(
            "Evidence-backed article",
            [CitationReferenceCodec.Format(document.Id, documentBlock.StableId)]);

        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(), [artifact], [], 0, 0, Sources: [web, document]));
        Http.OnGet(
            $"api/v1/campaigns/{CampaignId}/sources/{web.Id}/evidence",
            SourceEvidenceReviewTests.Revision(web, true, webBlock));
        Http.OnGet(
            $"api/v1/campaigns/{CampaignId}/sources/{document.Id}/evidence",
            SourceEvidenceReviewTests.Revision(document, true, documentBlock));

        var view = Render<MillFloorView>(parameters =>
            parameters.Add(component => component.CampaignId, CampaignId));
        view.WaitForAssertion(() =>
            Assert.Contains("Launch page evidence.", view.Markup, StringComparison.Ordinal));
        Assert.Equal(2, view.FindAll(".cm-source-tab").Count);
        view.WaitForAssertion(() =>
            Assert.False(Services.GetRequiredService<CampaignState>().IsLoading));
        await view.InvokeAsync(() => Task.CompletedTask);
        view.Render();

        view.Find($"[data-card='{artifact.Id}']").ParentElement!
            .TriggerEvent("onmouseenter", new MouseEventArgs());
        view.WaitForAssertion(() =>
            Assert.Equal(2, view.FindAll(".cm-source-tab").Count));
        Assert.Equal("true", view.FindAll(".cm-source-tab")[1].GetAttribute("aria-selected"));
        view.WaitForAssertion(() =>
            Assert.Contains("Measured proof from page three.", view.Markup, StringComparison.Ordinal));

        Assert.Contains("cm-evidence-block--active", view.Markup, StringComparison.Ordinal);
        Assert.Equal(
            $"/campaigns/{CampaignId}/floor?source={document.Id}&revision=1&evidence=page-0003",
            view.Find(".cm-evidence-block__link").GetAttribute("href"));
    }

    [Fact]
    public void Source_and_evidence_query_parameters_reopen_the_linked_block()
    {
        var web = SourceEvidenceReviewTests.Source(
            SourceKinds.WebPage, SourceModalities.Web, "Launch page", "https://example.com/launch")
            with { CampaignId = CampaignId };
        var document = SourceEvidenceReviewTests.Source(
            SourceKinds.Document, SourceModalities.Document, "Proof.pdf", null)
            with { CampaignId = CampaignId };
        var webBlock = SourceEvidenceReviewTests.Block(
            web.Id, "web-0001", "Launch page evidence.",
            EvidenceLocatorKinds.WebPageSection,
            """{"heading":"Launch","ordinal":1}""");
        var documentBlock = SourceEvidenceReviewTests.Block(
            document.Id, "page-0003", "Measured proof from page three.",
            EvidenceLocatorKinds.DocumentSection,
            """{"page":3}""");
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(), [], [], 0, 0, Sources: [web, document]));
        Http.OnGet(
            $"api/v1/campaigns/{CampaignId}/sources/{web.Id}/evidence",
            SourceEvidenceReviewTests.Revision(web, true, webBlock));
        Http.OnGet(
            $"api/v1/campaigns/{CampaignId}/sources/{document.Id}/evidence",
            SourceEvidenceReviewTests.Revision(document, true, documentBlock));

        Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"/campaigns/{CampaignId}/floor?source={document.Id}&revision=1&evidence={documentBlock.StableId}");
        var view = Render<Castmill.UI.App>();

        view.WaitForAssertion(() =>
            Assert.Contains("Measured proof from page three.", view.Markup, StringComparison.Ordinal));
        Assert.Equal("true", view.FindAll(".cm-source-tab")[1].GetAttribute("aria-selected"));
        Assert.Contains("cm-evidence-block--active", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_master_keeps_approved_text_while_review_modal_shows_current_draft()
    {
        var source = SourceEvidenceReviewTests.Source(
            SourceKinds.Document, SourceModalities.Document, "Proof.pdf", null)
            with { CampaignId = CampaignId };
        var approvedBlock = SourceEvidenceReviewTests.Block(
            source.Id, "page-0001", "Approved historical evidence.",
            EvidenceLocatorKinds.DocumentSection,
            """{"page":1}""");
        var draftBlock = approvedBlock with
        {
            Content = "Unapproved corrected evidence.",
            Revision = 2,
            RevisionId = Guid.NewGuid(),
            ApprovalState = EvidenceApprovalStates.Draft,
        };
        var currentSource = source with
        {
            CurrentEvidenceRevision = 2,
            CurrentEvidenceRevisionId = draftBlock.RevisionId,
        };
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(), [], [], 0, 0, Sources: [currentSource]));
        Http.OnGetQuery(
            $"api/v1/campaigns/{CampaignId}/sources/{source.Id}/evidence?approved=false",
            new EvidenceRevisionResponse(currentSource, 2, draftBlock.RevisionId, false, [draftBlock]));
        Http.OnGetQuery(
            $"api/v1/campaigns/{CampaignId}/sources/{source.Id}/evidence?approved=true",
            SourceEvidenceReviewTests.Revision(source, true, approvedBlock));

        var view = Render<MillFloorView>(parameters =>
            parameters.Add(component => component.CampaignId, CampaignId));
        view.WaitForAssertion(() =>
            Assert.Contains("Approved historical evidence.", view.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Unapproved corrected evidence.", view.Markup, StringComparison.Ordinal);

        view.FindAll("button").Single(button => button.TextContent.Trim() == "View").Click();
        view.WaitForAssertion(() =>
            Assert.Contains("Unapproved corrected evidence.", view.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pinned_artifact_renders_the_historical_approved_revision_it_consumed()
    {
        var source = SourceEvidenceReviewTests.Source(
            SourceKinds.WebPage, SourceModalities.Web, "Changing page", "https://example.com/page")
            with
            {
                CampaignId = CampaignId,
                CurrentEvidenceRevision = 2,
                CurrentEvidenceRevisionId = Guid.NewGuid(),
                ApprovedEvidence = new ApprovedEvidenceRevision(
                    Guid.Empty, 2, Guid.NewGuid(), "latest", DateTimeOffset.UtcNow),
            };
        source = source with
        {
            ApprovedEvidence = source.ApprovedEvidence! with { SourceAssetId = source.Id },
        };
        var historicalMarker = new ApprovedEvidenceRevision(
            source.Id, 1, Guid.NewGuid(), "historical", DateTimeOffset.UtcNow.AddMinutes(-5));
        var latestBlock = SourceEvidenceReviewTests.Block(
            source.Id, "web-0001", "Newly approved evidence.",
            EvidenceLocatorKinds.WebPageSection,
            """{"heading":"Result","ordinal":1}""") with
        {
            Revision = 2,
            RevisionId = source.ApprovedEvidence!.RevisionId,
        };
        var historicalBlock = latestBlock with
        {
            Content = "Historical evidence the artifact consumed.",
            Revision = 1,
            RevisionId = historicalMarker.RevisionId,
        };
        var artifact = Artifact(
            "Historical article",
            [CitationReferenceCodec.Format(source.Id, historicalBlock.StableId)],
            [historicalMarker]);
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(), [artifact], [], 0, 0, Sources: [source]));
        var latest = new EvidenceRevisionResponse(
            source, 2, latestBlock.RevisionId, true, [latestBlock]);
        Http.OnGetQuery(
            $"api/v1/campaigns/{CampaignId}/sources/{source.Id}/evidence?approved=false",
            latest);
        Http.OnGetQuery(
            $"api/v1/campaigns/{CampaignId}/sources/{source.Id}/evidence?approved=true",
            latest);
        Http.OnGetQuery(
            $"api/v1/campaigns/{CampaignId}/sources/{source.Id}/evidence?approved=false&revision=1",
            new EvidenceRevisionResponse(source, 1, historicalMarker.RevisionId, true, [historicalBlock]));

        var view = Render<MillFloorView>(parameters =>
            parameters.Add(component => component.CampaignId, CampaignId));
        view.WaitForAssertion(() =>
            Assert.Contains("Newly approved evidence.", view.Markup, StringComparison.Ordinal));
        view.WaitForAssertion(() =>
            Assert.False(Services.GetRequiredService<CampaignState>().IsLoading));
        await view.InvokeAsync(() => Task.CompletedTask);
        view.Render();
        view.Find($"[data-card='{artifact.Id}']").ParentElement!
            .TriggerEvent("onmouseenter", new MouseEventArgs());
        view.WaitForAssertion(() =>
            Assert.Contains("Historical evidence the artifact consumed.", view.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Newly approved evidence.", view.Markup, StringComparison.Ordinal);

        await Services.GetRequiredService<CampaignState>().RefreshAsync(CampaignId);
        view.WaitForAssertion(() =>
            Assert.Contains("Historical evidence the artifact consumed.", view.Markup, StringComparison.Ordinal));
        Assert.Equal(
            $"/campaigns/{CampaignId}/floor?source={source.Id}&revision=1&evidence=web-0001",
            view.Find(".cm-evidence-block__link").GetAttribute("href"));
    }

    private static CampaignResponse Campaign() => new(
        CampaignId,
        Guid.Parse("82222222-2222-2222-2222-222222222222"),
        "Multi-source campaign",
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private static ArtifactPreviewResponse Artifact(
        string title,
        IReadOnlyList<string> citations,
        IReadOnlyList<ApprovedEvidenceRevision>? evidence = null) => new(
        Guid.NewGuid(),
        CampaignId,
        "blog",
        title,
        ArtifactStatus.Draft,
        1,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        citations,
        Evidence: evidence);
}
