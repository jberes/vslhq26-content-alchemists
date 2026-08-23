using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Seo;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class DeepSeoFlowTests(CastmillApiFactory factory)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Analysis_is_persisted_and_must_be_approved_before_content_generation()
    {
        await using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seo:RequireAnalysisBeforeGeneration", "true");
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Scoped<ISeoResearch>(_ => new FixedResearch()));
                services.Replace(ServiceDescriptor.Scoped<ISeoProvider>(_ => new FixedSeoProvider()));
            });
        });
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"deep-seo-{Guid.NewGuid():N}@example.com",
                "correct-horse-battery-staple", "SEO Producer"));
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);

        var campaign = await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Analysis first", "Audience: engineering leaders")))
            .Content.ReadFromJsonAsync<CampaignResponse>();
        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaign!.Id}/transcripts",
            new { text = "We improved deployment speed. The product dashboard proves the change.", source = "test" });
        var ingestBody = await ingest.Content.ReadFromJsonAsync<IngestResponse>();

        var blocked = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign.Id}/generate/newsletter",
            new { transcriptArtifactId = ingestBody!.TranscriptArtifactId });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var blockedBrief = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign.Id}/brief?transcriptArtifactId={ingestBody.TranscriptArtifactId}",
            new { });
        Assert.Equal(HttpStatusCode.Conflict, blockedBrief.StatusCode);

        var analysis = await client.PostAsJsonAsync("/api/v1/seo/deep-analysis",
            new SeoDeepAnalysisRequest(campaign.Id, ingestBody.TranscriptArtifactId, "https://example.com"));
        Assert.Equal(HttpStatusCode.Created, analysis.StatusCode);
        var report = await analysis.Content.ReadFromJsonAsync<SeoAnalysisReportResponse>();
        Assert.Equal("react data grid", report!.Serp.Keyword);
        Assert.Equal(2, report.Serp.OrganicResults.Count);
        Assert.NotNull(report.Insights);
        Assert.Equal(66.7, report.Insights.Aeo.VisibilityPercent);
        Assert.Equal(3, report.Insights.Aeo.EnginesSucceeded);
        Assert.Single(report.Insights.RankedKeywords);
        Assert.NotNull(report.Insights.SiteAuthority);
        Assert.Equal(3, report.Insights.Competitors!.Count);
        Assert.Equal(0.62, report.Insights.Competitors.Single(c => c.Domain == "one.example").TopicVisibility);
        Assert.NotEmpty(report.Insights.ContentAngles);

        var artifacts = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts");
        Assert.Contains(artifacts!, a => a.Id == report.ReportArtifactId && a.Kind == "seo-report");
        var placeholder = Assert.Single(artifacts!, a => a.Kind == "blog");
        Assert.True(placeholder.IsPlaceholder);
        Assert.Contains(report.Insights.ContentAngles[0].Angle, placeholder.Title, StringComparison.Ordinal);

        await client.PutAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/seo-targets",
            new SeoTargetsRequest("react data grid", report.Research.Keywords, report.Research.Questions));

        artifacts = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts");
        Assert.Equal(ArtifactStatus.InReview, artifacts!.Single(a => a.Id == report.ReportArtifactId).Status);

        // Changing approved targets invalidates only the derived angles. The endpoint can
        // rebuild those angles without paying for another DataForSEO crawl.
        await client.PutAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/seo-targets",
            new SeoTargetsRequest("react grid tutorial",
                [new SeoTarget("react grid tutorial", 2200, 28, 78, "provider")],
                report.Research.Questions));
        var storedReport = await ReadReportAsync(client, campaign.Id, report.ReportArtifactId);
        Assert.True(storedReport.AnglesStale);
        Assert.False(storedReport.InputsStale);

        var regenerate = await client.PostAsJsonAsync(
            $"/api/v1/seo/reports/{report.ReportArtifactId}/angles/regenerate", new { });
        regenerate.EnsureSuccessStatusCode();
        storedReport = await ReadReportAsync(client, campaign.Id, report.ReportArtifactId);
        Assert.False(storedReport.AnglesStale);

        // A brief/content-type change invalidates the research inputs themselves.
        await client.PutAsJsonAsync($"/api/v1/campaigns/{campaign.Id}",
            new CampaignUpdateRequest(campaign.Name, "Audience: technical founders",
                Status: CampaignStatus.Ready, ContentType: CampaignContentType.Tutorial));
        storedReport = await ReadReportAsync(client, campaign.Id, report.ReportArtifactId);
        Assert.True(storedReport.InputsStale);

        var allowed = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign.Id}/generate/newsletter",
            new { transcriptArtifactId = ingestBody.TranscriptArtifactId });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Evidence_only_source_uses_the_same_analysis_gate_and_press_run_contract()
    {
        await using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seo:RequireAnalysisBeforeGeneration", "true");
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Scoped<ISeoResearch>(_ => new FixedResearch()));
                services.Replace(ServiceDescriptor.Scoped<ISeoProvider>(_ => new FixedSeoProvider()));
                services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(
                    _ => new AiGenerationTests.GeneralizedEvidenceFoundryFactory()));
            });
        });
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(
                $"evidence-gate-{Guid.NewGuid():N}@example.com",
                "correct-horse-battery-staple",
                "Evidence Gate Producer"));
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        var campaign = (await (await client.PostAsJsonAsync(
            "/api/v1/campaigns",
            new CampaignCreateRequest(
                "Evidence-only launch",
                "Audience: engineering leaders",
                Intent: CampaignIntent.Launch,
                OutputRecipe: ["social-x"])))
            .Content.ReadFromJsonAsync<CampaignResponse>())!;
        var sourceArtifact = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/artifacts",
            new ArtifactCreateRequest(
                "campaign-summary",
                "Rollout evidence",
                """{"summary":"The product dashboard cut deployment time in half for platform teams."}""")))
            .Content.ReadFromJsonAsync<ArtifactResponse>())!;
        var imported = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/sources/import/artifact",
            new ArtifactSourceImportRequest(sourceArtifact.Id));
        imported.EnsureSuccessStatusCode();

        var context = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign.Id}/research-context", new { });
        Assert.True(context.IsSuccessStatusCode, await context.Content.ReadAsStringAsync());
        var suggestion = await context.Content.ReadFromJsonAsync<ResearchContextSuggestionResponse>();
        Assert.False(string.IsNullOrWhiteSpace(suggestion!.Audience));

        var blocked = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign.Id}/generate/social-x",
            new { brief = "Launch proof" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        var analysis = await client.PostAsJsonAsync(
            "/api/v1/seo/deep-analysis",
            new SeoDeepAnalysisRequest(campaign.Id, SiteUrl: "https://example.com"));
        analysis.EnsureSuccessStatusCode();
        var report = (await analysis.Content.ReadFromJsonAsync<SeoAnalysisReportResponse>())!;
        var approve = await client.PutAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/seo-targets",
            new SeoTargetsRequest(
                report.Research.Keywords[0].Term,
                report.Research.Keywords,
                report.Research.Questions));
        approve.EnsureSuccessStatusCode();

        var brief = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign.Id}/brief?title=Evidence-only%20launch",
            new { });
        brief.EnsureSuccessStatusCode();

        var allowed = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign.Id}/generate/social-x",
            new { brief = "Launch proof" });
        allowed.EnsureSuccessStatusCode();
        var result = await allowed.Content.ReadFromJsonAsync<GenerationResult>();
        Assert.True(result!.Success, result.Error);
    }

    [Fact]
    public async Task Explicit_campaign_opt_out_bypasses_required_analysis_without_a_site_url()
    {
        await using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seo:RequireAnalysisBeforeGeneration", "true");
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(
                    _ => new AiGenerationTests.GeneralizedEvidenceFoundryFactory()));
            });
        });
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(
                $"skip-seo-{Guid.NewGuid():N}@example.com",
                "correct-horse-battery-staple",
                "Source-first Producer"));
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth!.AccessToken);

        var campaign = (await (await client.PostAsJsonAsync(
            "/api/v1/campaigns",
            new CampaignCreateRequest(
                "Source-first campaign",
                "Audience: product leaders",
                Intent: CampaignIntent.Repurpose,
                OutputRecipe: ["social-x"],
                SkipSeoAnalysis: true)))
            .Content.ReadFromJsonAsync<CampaignResponse>())!;
        Assert.True(campaign.SkipSeoAnalysis);

        var sourceArtifact = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/artifacts",
            new ArtifactCreateRequest(
                "campaign-summary",
                "Source proof",
                """{"summary":"The rollout reduced setup time for product teams."}""")))
            .Content.ReadFromJsonAsync<ArtifactResponse>())!;
        (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/sources/import/artifact",
            new ArtifactSourceImportRequest(sourceArtifact.Id))).EnsureSuccessStatusCode();

        var brief = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign.Id}/brief?title=Source-first%20campaign",
            new { });
        brief.EnsureSuccessStatusCode();

        var generated = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign.Id}/generate/social-x",
            new { brief = "Lead with the measured result" });
        generated.EnsureSuccessStatusCode();
        var result = await generated.Content.ReadFromJsonAsync<GenerationResult>();
        Assert.True(result!.Success, result.Error);

        var artifacts = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts");
        Assert.DoesNotContain(artifacts!, artifact => artifact.Kind == "seo-report");
    }

    [Fact]
    public async Task Impact_review_tracks_approved_inputs_and_requires_explicit_per_item_decisions()
    {
        await using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seo:RequireAnalysisBeforeGeneration", "true");
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Scoped<ISeoResearch>(_ => new FixedResearch()));
                services.Replace(ServiceDescriptor.Scoped<ISeoProvider>(_ => new FixedSeoProvider()));
                services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(
                    _ => new AiGenerationTests.FakeFoundryFactory()));
            });
        });
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"impact-{Guid.NewGuid():N}@example.com",
                "correct-horse-battery-staple", "Impact Producer"));
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth!.AccessToken);

        var campaign = await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Impact review", "Audience: engineering leaders")))
            .Content.ReadFromJsonAsync<CampaignResponse>();
        var ingest = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign!.Id}/transcripts",
            new TranscriptIngestRequest("Proof one. Proof two. Proof three.", "test"));
        ingest.EnsureSuccessStatusCode();
        var ingestBody = await ingest.Content.ReadFromJsonAsync<IngestResponse>();
        var source = Assert.Single((await client.GetFromJsonAsync<List<SourceAssetResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/sources"))!);

        var analysis = await client.PostAsJsonAsync("/api/v1/seo/deep-analysis",
            new SeoDeepAnalysisRequest(campaign.Id, ingestBody!.TranscriptArtifactId, "https://example.com"));
        analysis.EnsureSuccessStatusCode();
        var report = (await analysis.Content.ReadFromJsonAsync<SeoAnalysisReportResponse>())!;
        var approve = await client.PutAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/seo-targets",
            new SeoTargetsRequest("react data grid", report.Research.Keywords, report.Research.Questions));
        approve.EnsureSuccessStatusCode();

        var generate = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign.Id}/generate/newsletter",
            new { transcriptArtifactId = ingestBody.TranscriptArtifactId });
        generate.EnsureSuccessStatusCode();
        var generated = (await generate.Content.ReadFromJsonAsync<GenerationResult>())!;
        Assert.True(generated.Success, generated.Error);
        var artifactId = generated.ArtifactId!.Value;

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
            Assert.Contains(await db.ContentDependencySnapshots.IgnoreQueryFilters().ToListAsync(),
                snapshot => snapshot.ArtifactId == report.ReportArtifactId
                    && snapshot.Reason == ContentDependencyReasons.DeepAnalysis);
            var generatedSnapshot = Assert.Single(
                await db.ContentDependencySnapshots.IgnoreQueryFilters()
                    .Where(snapshot => snapshot.ArtifactId == artifactId && snapshot.IsCurrent)
                    .ToListAsync());
            var recordedMarker = Assert.Single(
                await db.ContentEvidenceDependencies.IgnoreQueryFilters()
                    .Where(marker => marker.SnapshotId == generatedSnapshot.Id)
                    .ToListAsync());
            Assert.Equal(source.ApprovedEvidence!.SourceAssetId, recordedMarker.SourceAssetId);
            Assert.Equal(source.ApprovedEvidence.Revision, recordedMarker.Revision);
            Assert.Equal(source.ApprovedEvidence.RevisionId, recordedMarker.RevisionId);
            Assert.Equal(source.ApprovedEvidence.Hash, recordedMarker.Hash);
            Assert.Equal(source.ApprovedEvidence.ApprovedAt, recordedMarker.ApprovedAt);
        }

        var review = await client.GetFromJsonAsync<ContentImpactReviewResponse>(
            $"/api/v1/campaigns/{campaign.Id}/impact-review");
        Assert.DoesNotContain(review!.Artifacts,
            item => item.Kind is "transcript" or "seo-report");
        Assert.Equal(ContentStalenessStates.Fresh,
            Assert.Single(review.Artifacts, item => item.ArtifactId == artifactId).State);

        var revise = await client.PatchAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/sources/{source.Id}/evidence/S1",
            new EvidenceBlockRevisionRequest(null, true));
        revise.EnsureSuccessStatusCode();
        review = await client.GetFromJsonAsync<ContentImpactReviewResponse>(
            $"/api/v1/campaigns/{campaign.Id}/impact-review");
        Assert.Equal(ContentStalenessStates.Fresh,
            Assert.Single(review!.Artifacts, item => item.ArtifactId == artifactId).State);

        var approveEvidence = await client.PostAsync(
            $"/api/v1/campaigns/{campaign.Id}/sources/{source.Id}/evidence/2/approve", null);
        approveEvidence.EnsureSuccessStatusCode();
        review = await client.GetFromJsonAsync<ContentImpactReviewResponse>(
            $"/api/v1/campaigns/{campaign.Id}/impact-review");
        Assert.Equal(ContentStalenessStates.EvidenceChanged,
            Assert.Single(review!.Artifacts, item => item.ArtifactId == artifactId).State);

        var changeTargets = await client.PutAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/seo-targets",
            new SeoTargetsRequest("react grid tutorial",
                [new SeoTarget("react grid tutorial")], report.Research.Questions));
        changeTargets.EnsureSuccessStatusCode();
        review = await client.GetFromJsonAsync<ContentImpactReviewResponse>(
            $"/api/v1/campaigns/{campaign.Id}/impact-review");
        Assert.Equal(ContentStalenessStates.BothChanged,
            Assert.Single(review!.Artifacts, item => item.ArtifactId == artifactId).State);

        var beforeKeep = await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{artifactId}");
        var keep = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/impact-review/{artifactId}/keep", new { });
        keep.EnsureSuccessStatusCode();
        var afterKeep = await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{artifactId}");
        Assert.Equal(beforeKeep!.ContentJson, afterKeep!.ContentJson);
        Assert.Equal(beforeKeep.Version, afterKeep.Version);
        var kept = await keep.Content.ReadFromJsonAsync<ContentImpactActionResponse>();
        Assert.Equal(ContentStalenessStates.Fresh, kept!.Impact.State);

        changeTargets = await client.PutAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/seo-targets",
            new SeoTargetsRequest("react grid guide",
                [new SeoTarget("react grid guide")], report.Research.Questions));
        changeTargets.EnsureSuccessStatusCode();
        review = await client.GetFromJsonAsync<ContentImpactReviewResponse>(
            $"/api/v1/campaigns/{campaign.Id}/impact-review");
        Assert.Equal(ContentStalenessStates.StrategyChanged,
            Assert.Single(review!.Artifacts, item => item.ArtifactId == artifactId).State);

        var regenerate = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/impact-review/{artifactId}/regenerate", new { });
        regenerate.EnsureSuccessStatusCode();
        var regenerated = await regenerate.Content.ReadFromJsonAsync<ContentImpactActionResponse>();
        Assert.Equal(ContentStalenessStates.Fresh, regenerated!.Impact.State);
        var afterRegenerate = await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{artifactId}");
        Assert.True(afterRegenerate!.Version > afterKeep.Version);
        var revisions = await client.GetFromJsonAsync<List<ArtifactRevisionResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{artifactId}/revisions");
        var keptRevision = Assert.Single(revisions!, revision => revision.Version == afterKeep.Version);

        var restore = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{artifactId}/revisions/{keptRevision.Id}/restore");
        restore.Headers.TryAddWithoutValidation("If-Match", $"\"{afterRegenerate.Version}\"");
        var restoreResponse = await client.SendAsync(restore);
        restoreResponse.EnsureSuccessStatusCode();
        var restored = await restoreResponse.Content.ReadFromJsonAsync<ArtifactResponse>();
        Assert.Equal(afterKeep.ContentJson, restored!.ContentJson);

        review = await client.GetFromJsonAsync<ContentImpactReviewResponse>(
            $"/api/v1/campaigns/{campaign.Id}/impact-review");
        Assert.Equal(ContentStalenessStates.StrategyChanged,
            Assert.Single(review!.Artifacts, item => item.ArtifactId == artifactId).State);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
            var revisionRow = await db.ArtifactRevisions.IgnoreQueryFilters()
                .SingleAsync(revision => revision.Id == keptRevision.Id);
            Assert.NotNull(revisionRow.ContentDependencySnapshotId);
            var historical = await db.ContentDependencySnapshots.IgnoreQueryFilters()
                .SingleAsync(snapshot => snapshot.Id == revisionRow.ContentDependencySnapshotId);
            var restoredDependency = await db.ContentDependencySnapshots.IgnoreQueryFilters()
                .SingleAsync(snapshot => snapshot.ArtifactId == artifactId && snapshot.IsCurrent);
            Assert.NotEqual(historical.Id, restoredDependency.Id);
            Assert.Equal(ContentDependencyReasons.Restored, restoredDependency.Reason);
            Assert.Equal(historical.ApprovedReportHash, restoredDependency.ApprovedReportHash);
            Assert.Equal(historical.ApprovedTargetStrategyHash,
                restoredDependency.ApprovedTargetStrategyHash);
        }

        var other = app.CreateClient();
        register = await other.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"impact-other-{Guid.NewGuid():N}@example.com",
                "correct-horse-battery-staple", "Other Producer"));
        register.EnsureSuccessStatusCode();
        auth = await register.Content.ReadFromJsonAsync<AuthResponse>();
        other.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.GetAsync($"/api/v1/campaigns/{campaign.Id}/impact-review")).StatusCode);

        var legacyCampaign = await (await other.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Legacy campaign", null)))
            .Content.ReadFromJsonAsync<CampaignResponse>();
        var legacyArtifact = await (await other.PostAsJsonAsync(
            $"/api/v1/campaigns/{legacyCampaign!.Id}/artifacts",
            new ArtifactCreateRequest("blog", "Legacy blog", "{\"markdown\":\"old\"}")))
            .Content.ReadFromJsonAsync<ArtifactResponse>();
        var legacyReview = await other.GetFromJsonAsync<ContentImpactReviewResponse>(
            $"/api/v1/campaigns/{legacyCampaign.Id}/impact-review");
        var legacyImpact = Assert.Single(legacyReview!.Artifacts,
            item => item.ArtifactId == legacyArtifact!.Id);
        Assert.Equal(ContentStalenessStates.Unknown, legacyImpact.State);
        Assert.False(legacyImpact.CanAcknowledge);
        Assert.False(legacyImpact.CanRegenerate);
    }

    private sealed record IngestResponse(Guid TranscriptArtifactId, int SegmentCount);

    private static async Task<SeoAnalysisReportResponse> ReadReportAsync(
        HttpClient client, Guid campaignId, Guid artifactId)
    {
        var artifact = await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaignId}/artifacts/{artifactId}");
        return JsonSerializer.Deserialize<SeoAnalysisReportResponse>(artifact!.ContentJson, WebJson)!;
    }

    private sealed class FixedResearch : ISeoResearch
    {
        public Task<SeoResearchResponse> ResearchAsync(
            Guid userId, TranscriptContent transcript, string? campaignName, CancellationToken ct) =>
            Task.FromResult(new SeoResearchResponse(
                [new SeoTarget("react data grid", 8100, 42, 157.7, "provider")],
                [new SeoQuestion("How do you paginate a React data grid?", "paa")],
                true, []));
    }

    private sealed class FixedSeoProvider : ISeoProvider
    {
        public bool IsConfigured => true;
        public Task<IReadOnlyList<SeoKeyword>> GetKeywordMetricsAsync(IReadOnlyList<string> keywords, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeoKeyword>>([]);
        public Task<IReadOnlyList<SeoKeyword>> GetSuggestionsAsync(string seedKeyword, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeoKeyword>>([]);
        public Task<SeoAnalysis> AnalyzeAsync(string keyword, string? targetUrl, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetQuestionsAsync(string keyword, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<SeoSerpSnapshot> GetSerpSnapshotAsync(string keyword, CancellationToken ct) =>
            Task.FromResult(new SeoSerpSnapshot(keyword, "AI overview", "Featured answer",
                [new SeoSerpResult(1, "Leader one", "https://one.example", "one.example"),
                 new SeoSerpResult(2, "Leader two", "https://two.example", "two.example")]));
        public Task<IReadOnlyList<SeoRankedKeyword>> GetRankedKeywordsAsync(
            string domain, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeoRankedKeyword>>(
                [new SeoRankedKeyword("existing query", 7, 900, 24, 30, "https://example.com/existing", "informational")]);
        public Task<SeoAuthoritySnapshot?> GetAuthorityAsync(string domain, CancellationToken ct) =>
            Task.FromResult<SeoAuthoritySnapshot?>(
                new SeoAuthoritySnapshot(domain, 42, 1200, 160, 120, 4, 2));
        public Task<SeoPositionFootprint?> GetPositionFootprintAsync(string domain, CancellationToken ct) =>
            Task.FromResult<SeoPositionFootprint?>(new SeoPositionFootprint(3, 8, 20, 100, 450));
        public Task<IReadOnlyList<SeoCompetitorCandidate>> GetSerpCompetitorsAsync(
            IReadOnlyList<string> keywords, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeoCompetitorCandidate>>(
                [new SeoCompetitorCandidate("one.example", 2.4, 8, 0.62, 440),
                 new SeoCompetitorCandidate("two.example", 4.2, 6, 0.41, 260),
                 new SeoCompetitorCandidate("example.com", 8, 2, 0.12, 40)]);
        public Task<SeoAeoEngineResult> QueryAnswerEngineAsync(
            string provider, string question, string? siteDomain, CancellationToken ct) =>
            Task.FromResult(provider == "perplexity"
                ? new SeoAeoEngineResult(provider, provider, false, false, null, [], "Unavailable")
                : new SeoAeoEngineResult(
                    provider, provider, true, provider is "chat_gpt" or "claude", "Answer",
                    provider is "chat_gpt" or "claude"
                        ? [new SeoCitation("Own", "https://example.com/article", "example.com", true)]
                        : []));
    }
}
