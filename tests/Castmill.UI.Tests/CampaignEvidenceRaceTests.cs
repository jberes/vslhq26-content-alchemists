using System.Net;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.State;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

public sealed class CampaignEvidenceRaceTests : CastmillUiTestContext
{
    [Fact]
    public async Task Delayed_evidence_from_previous_campaign_cannot_overwrite_new_campaign()
    {
        var firstCampaign = Guid.NewGuid();
        var secondCampaign = Guid.NewGuid();
        var firstSource = SourceEvidenceReviewTests.Source(
            SourceKinds.Document, SourceModalities.Document, "First source", null)
            with { CampaignId = firstCampaign };
        var secondSource = SourceEvidenceReviewTests.Source(
            SourceKinds.WebPage, SourceModalities.Web, "Second source", "https://example.com")
            with { CampaignId = secondCampaign };
        var firstEvidence = SourceEvidenceReviewTests.Revision(
            firstSource,
            true,
            SourceEvidenceReviewTests.Block(
                firstSource.Id,
                "first-0001",
                "First campaign evidence.",
                EvidenceLocatorKinds.DocumentSection,
                """{"section":1}"""));
        var secondEvidence = SourceEvidenceReviewTests.Revision(
            secondSource,
            true,
            SourceEvidenceReviewTests.Block(
                secondSource.Id,
                "second-0001",
                "Second campaign evidence.",
                EvidenceLocatorKinds.WebPageSection,
                """{"heading":"Second"}"""));
        Http.OnGet($"api/v1/campaigns/{firstCampaign}/preview", new CampaignPreview(
            Campaign(firstCampaign, "First"), [], [], 0, 0, Sources: [firstSource]));
        Http.OnGet($"api/v1/campaigns/{secondCampaign}/preview", new CampaignPreview(
            Campaign(secondCampaign, "Second"), [], [], 0, 0, Sources: [secondSource]));
        var firstGate = Http.Gate(
            HttpMethod.Get,
            $"api/v1/campaigns/{firstCampaign}/sources/{firstSource.Id}/evidence?approved=false");
        Http.OnGetQuery(
            $"api/v1/campaigns/{firstCampaign}/sources/{firstSource.Id}/evidence?approved=true",
            firstEvidence);
        Http.OnGetQuery(
            $"api/v1/campaigns/{secondCampaign}/sources/{secondSource.Id}/evidence?approved=false",
            secondEvidence);
        Http.OnGetQuery(
            $"api/v1/campaigns/{secondCampaign}/sources/{secondSource.Id}/evidence?approved=true",
            secondEvidence);

        var state = Services.GetRequiredService<CampaignState>();
        var firstLoad = state.LoadAsync(firstCampaign);
        await firstLoad;
        var firstDetails = state.WhenDetailsLoadedAsync();
        await WaitUntilAsync(() => Http.Requests.Any(request =>
            request.RequestUri?.AbsolutePath.Contains(firstSource.Id.ToString(), StringComparison.Ordinal) == true));
        await state.LoadAsync(secondCampaign);
        var secondDetails = state.WhenDetailsLoadedAsync();
        await secondDetails;
        firstGate.SetResult(StubHttpHandler.Json(firstEvidence));
        await firstDetails;

        Assert.Equal(secondCampaign, state.CampaignId);
        Assert.Equal("Second", state.Campaign?.Name);
        Assert.Single(state.Sources);
        Assert.Equal(secondSource.Id, state.Sources[0].Id);
        Assert.True(state.Evidence.ContainsKey(secondSource.Id));
        Assert.False(state.Evidence.ContainsKey(firstSource.Id));
    }

    private static CampaignResponse Campaign(Guid id, string name) => new(
        id,
        Guid.NewGuid(),
        name,
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var stop = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition() && DateTimeOffset.UtcNow < stop)
        {
            await Task.Yield();
        }
        Assert.True(condition(), "The expected request did not start.");
    }
}
