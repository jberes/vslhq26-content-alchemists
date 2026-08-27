using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Castmill.Api.Services.Ai;
using Castmill.Core.Ai;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Castmill.Api.Tests;

/// <summary>
/// Exercises the full B5 orchestration through HTTP with a canned-response
/// model behind the IFoundryClientFactory seam — proving ingest → fan-out →
/// validation → persistence without any real AI spend.
/// </summary>
[Collection("api")]
public sealed class AiGenerationTests(CastmillApiFactory factory)
{
    private WebApplicationFactory<Program> WithFakeModel(bool malformedYoutubeTaxonomy = false) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(
                _ => new FakeFoundryFactory(malformedYoutubeTaxonomy)))));

    private static async Task<(HttpClient Client, Guid CampaignId, Guid TranscriptId)> SetUpAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"ai-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "AI Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var campaign = await client.PostAsJsonAsync("/api/v1/campaigns", new CampaignCreateRequest("AI campaign", null));
        var campaignId = (await campaign.Content.ReadFromJsonAsync<CampaignResponse>())!.Id;

        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/transcripts",
            new { text = "We launched the new product. It cut deployment time in half. Customers love the new dashboard. The team shipped it in six weeks.", source = "unit-test" });
        ingest.EnsureSuccessStatusCode();
        var transcriptId = (await ingest.Content.ReadFromJsonAsync<IngestResponse>())!.TranscriptArtifactId;
        return (client, campaignId, transcriptId);
    }

    [Fact]
    public async Task Full_fan_out_generates_validated_artifacts_with_citations()
    {
        await using var app = WithFakeModel();
        var (client, campaignId, transcriptId) = await SetUpAsync(app);

        var response = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate",
            new { transcriptArtifactId = transcriptId, brief = "Product launch" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("blog;dur=", response.Headers.GetValues("Server-Timing").Single(), StringComparison.Ordinal);

        var body = await response.Content.ReadFromJsonAsync<FanOutResponse>();
        Assert.NotNull(body);
        Assert.Equal(0, body.Failed);
        // Every fan-out generator, plus blog (which runs its own outline→draft→audit pipeline
        // and so is not in FanOut). Derived rather than hard-coded: a literal here goes stale
        // the moment a generator is added, and reads as a regression rather than a new kind.
        var millFanOutCount = Generators.FanOut.Count(spec => spec.Kind != "seo-brief");
        Assert.Equal(millFanOutCount + 1, body.Succeeded);

        // Every artifact persisted; previews list them all (plus the transcript).
        var previews = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaignId}/artifacts");
        Assert.Equal(millFanOutCount + 2, previews!.Count);
        Assert.Contains(previews, p => p.Kind == "blog");
        Assert.Contains(previews, p => p.Kind == "social-x");
        Assert.Contains(previews, p => p.Kind == "image-prompts");
        Assert.DoesNotContain(previews, p => p.Kind == "seo-brief");

        var source = Assert.Single((await client.GetFromJsonAsync<List<SourceAssetResponse>>(
            $"/api/v1/campaigns/{campaignId}/sources"))!);
        foreach (var citation in previews
            .Where(preview => preview.Kind != "transcript")
            .SelectMany(preview => preview.Citations ?? []))
        {
            Assert.True(CitationReferenceCodec.TryParse(citation, out var reference));
            Assert.Equal(source.Id, reference.SourceAssetId);
        }
    }

    /// <summary>
    /// "Three more LinkedIn posts." Kinds is a SET server-side — repeating the kind in the
    /// array still generates it once — so the count is its own field, and each copy lands as
    /// its own artifact row rather than overwriting the last.
    /// </summary>
    [Fact]
    public async Task A_count_prints_that_many_of_each_requested_kind()
    {
        await using var app = WithFakeModel();
        var (client, campaignId, transcriptId) = await SetUpAsync(app);

        var response = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate",
            new
            {
                transcriptArtifactId = transcriptId,
                brief = "Angle this at pricing objections",
                kinds = new[] { "social-linkedin" },
                count = 3,
            });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<FanOutResponse>();
        Assert.Equal(3, body!.Succeeded);

        var previews = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaignId}/artifacts");
        var posts = previews!.Where(p => p.Kind == "social-linkedin").ToList();
        Assert.Equal(3, posts.Count);
        // Three distinct rows, not one row saved three times.
        Assert.Equal(3, posts.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public async Task Generator_can_cite_a_second_approved_source_with_overlapping_local_ids()
    {
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(
                _ => new PromptEvidenceFoundryFactory()))));
        var (client, campaignId, transcriptId) = await SetUpAsync(app);

        var secondIngest = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/transcripts",
            new
            {
                text = "A second source carries separate approved proof. It shares local segment ids.",
                source = "second-source",
            });
        secondIngest.EnsureSuccessStatusCode();
        var sources = (await client.GetFromJsonAsync<List<SourceAssetResponse>>(
            $"/api/v1/campaigns/{campaignId}/sources"))!;
        Assert.Equal(2, sources.Count);

        var generation = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/generate/social-x",
            new { transcriptArtifactId = transcriptId });
        generation.EnsureSuccessStatusCode();
        var result = await generation.Content.ReadFromJsonAsync<GenerationResult>();
        Assert.True(result!.Success, result.Error);

        var previews = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaignId}/artifacts");
        var post = Assert.Single(previews!, preview => preview.Kind == "social-x");
        var citation = Assert.Single(post.Citations!);
        Assert.True(CitationReferenceCodec.TryParse(citation, out var reference));
        Assert.Equal(sources[^1].Id, reference.SourceAssetId);
    }

    [Fact]
    public async Task Clip_boundaries_and_citations_stay_on_the_selected_transcript_source()
    {
        await using var app = WithFakeModel();
        var (client, campaignId, transcriptId) = await SetUpAsync(app);
        var originalSource = Assert.Single((await client.GetFromJsonAsync<List<SourceAssetResponse>>(
            $"/api/v1/campaigns/{campaignId}/sources"))!);
        var secondIngest = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/transcripts",
            new
            {
                text = "A second source reuses S1. It reuses S2. It reuses S3.",
                source = "overlapping-source",
            });
        secondIngest.EnsureSuccessStatusCode();

        var generation = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/generate/clip-suggestions",
            new { transcriptArtifactId = transcriptId });
        var result = await generation.Content.ReadFromJsonAsync<GenerationResult>();
        Assert.True(result!.Success, result.Error);

        var artifact = await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaignId}/artifacts/{result.ArtifactId}");
        using var content = JsonDocument.Parse(artifact!.ContentJson);
        var payload = content.RootElement.GetProperty("content");
        var citation = Assert.Single(payload.GetProperty("citations").EnumerateArray()).GetString()!;
        Assert.True(CitationReferenceCodec.TryParse(citation, out var reference));
        Assert.Equal(originalSource.Id, reference.SourceAssetId);
        var clip = Assert.Single(payload.GetProperty("clips").EnumerateArray());
        Assert.Equal(0, clip.GetProperty("inSeconds").GetDouble());
        Assert.True(clip.GetProperty("outSeconds").GetDouble() > 0);
    }

    [Fact]
    public async Task Seo_brief_is_not_accepted_as_a_mill_content_type()
    {
        await using var app = WithFakeModel();
        var (client, campaignId, transcriptId) = await SetUpAsync(app);

        var batch = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate",
            new { transcriptArtifactId = transcriptId, kinds = new[] { "seo-brief" } });
        var single = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/generate/seo-brief",
            new { transcriptArtifactId = transcriptId });

        Assert.Equal(HttpStatusCode.BadRequest, batch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, single.StatusCode);
    }

    [Fact]
    public async Task Youtube_runs_outline_draft_audit_and_can_regenerate_one_scored_slot()
    {
        await using var app = WithFakeModel();
        var (client, campaignId, transcriptId) = await SetUpAsync(app);
        var generate = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/generate/youtube",
            new { transcriptArtifactId = transcriptId, brief = "Use the approved search intent" });
        generate.EnsureSuccessStatusCode();
        var result = await generate.Content.ReadFromJsonAsync<GenerationResult>();
        Assert.True(result!.Success);

        var artifact = await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaignId}/artifacts/{result.ArtifactId}");
        using var package = JsonDocument.Parse(artifact!.ContentJson);
        var content = package.RootElement.GetProperty("content");
        Assert.Equal(3, content.GetProperty("titleOptions").GetArrayLength());
        Assert.Equal(["A", "B", "C"], content.GetProperty("titleOptions")
            .EnumerateArray().Select(option => option.GetProperty("slot").GetString()));
        Assert.EndsWith("?", content.GetProperty("suggestedPinnedComment").GetString(), StringComparison.Ordinal);
        Assert.True(content.GetProperty("audit").GetProperty("hookWithin125").GetBoolean());

        var regenerate = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/artifacts/{artifact.Id}/youtube-titles/B/regenerate",
            new YoutubeTitleRegenerationRequest("Make the curiosity gap more concrete"));
        regenerate.EnsureSuccessStatusCode();
        var regenerated = await regenerate.Content.ReadFromJsonAsync<YoutubeTitleRegenerationResponse>();
        Assert.Equal("B", regenerated!.Option.Slot);
        Assert.Equal(89, regenerated.Option.Score);

        artifact = await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaignId}/artifacts/{artifact.Id}");
        Assert.Contains("What Halved Our Deployment Time?", artifact!.ContentJson, StringComparison.Ordinal);
        var revisions = await client.GetFromJsonAsync<List<ArtifactRevisionResponse>>(
            $"/api/v1/campaigns/{campaignId}/artifacts/{artifact.Id}/revisions");
        Assert.Contains(revisions!, revision => revision.Reason == "youtube-title-b");
    }

    [Fact]
    public async Task Youtube_repairs_model_taxonomy_synonyms_and_duplicate_angles()
    {
        await using var app = WithFakeModel(malformedYoutubeTaxonomy: true);
        var (client, campaignId, transcriptId) = await SetUpAsync(app);

        var generate = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/generate/youtube",
            new { transcriptArtifactId = transcriptId });
        generate.EnsureSuccessStatusCode();
        var result = await generate.Content.ReadFromJsonAsync<GenerationResult>();
        Assert.True(result!.Success, result.Error);

        var artifact = await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaignId}/artifacts/{result.ArtifactId}");
        using var package = JsonDocument.Parse(artifact!.ContentJson);
        var options = package.RootElement.GetProperty("content").GetProperty("titleOptions");
        Assert.Equal(["A", "B", "C"], options.EnumerateArray()
            .Select(option => option.GetProperty("slot").GetString()));
        Assert.Equal(["seo", "curiosity", "problem-solution"], options.EnumerateArray()
            .Select(option => option.GetProperty("angle").GetString()));
    }

    [Fact]
    public async Task Youtube_title_regeneration_merges_a_new_source_citation_into_the_package()
    {
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(
                _ => new TitleEvidenceFoundryFactory()))));
        var (client, campaignId, transcriptId) = await SetUpAsync(app);
        var generate = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/generate/youtube",
            new { transcriptArtifactId = transcriptId });
        var generated = await generate.Content.ReadFromJsonAsync<GenerationResult>();
        Assert.True(generated!.Success, generated.Error);
        var before = await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaignId}/artifacts/{generated.ArtifactId}");
        using var beforePackage = JsonDocument.Parse(before!.ContentJson);
        var originalCitations = beforePackage.RootElement.GetProperty("content")
            .GetProperty("citations")
            .EnumerateArray()
            .Select(citation => citation.GetString()!)
            .ToList();

        var secondIngest = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/transcripts",
            new { text = "Second-source proof for a stronger title.", source = "title-source" });
        secondIngest.EnsureSuccessStatusCode();
        var sources = (await client.GetFromJsonAsync<List<SourceAssetResponse>>(
            $"/api/v1/campaigns/{campaignId}/sources"))!;

        var regenerate = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/artifacts/{generated.ArtifactId}/youtube-titles/B/regenerate",
            new YoutubeTitleRegenerationRequest("Use the new evidence"));
        regenerate.EnsureSuccessStatusCode();

        var artifact = await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaignId}/artifacts/{generated.ArtifactId}");
        using var package = JsonDocument.Parse(artifact!.ContentJson);
        var citations = package.RootElement.GetProperty("content").GetProperty("citations")
            .EnumerateArray()
            .Select(citation => citation.GetString()!)
            .ToList();
        Assert.All(originalCitations, citation => Assert.Contains(citation, citations));
        Assert.Contains(citations, citation =>
            CitationReferenceCodec.TryParse(citation, out var reference)
            && reference.SourceAssetId == sources[^1].Id);
    }

    [Fact]
    public async Task Imported_evidence_supports_generation_title_regeneration_and_tech_edit_without_transcript()
    {
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(
                _ => new GeneralizedEvidenceFoundryFactory()))));
        var (client, campaignId) = await SetUpCampaignWithoutTranscriptAsync(app);
        var sourceArtifact = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/artifacts",
            new ArtifactCreateRequest(
                "campaign-summary",
                "Imported proof",
                """{"summary":"The rollout reduced recovery time by forty percent."}"""));
        sourceArtifact.EnsureSuccessStatusCode();
        var sourceArtifactBody = (await sourceArtifact.Content.ReadFromJsonAsync<ArtifactResponse>())!;
        var imported = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/sources/import/artifact",
            new ArtifactSourceImportRequest(sourceArtifactBody.Id));
        imported.EnsureSuccessStatusCode();
        var source = (await imported.Content.ReadFromJsonAsync<EvidenceRevisionResponse>())!.Source;

        var youtube = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/generate/youtube",
            new { brief = "Use approved evidence" });
        youtube.EnsureSuccessStatusCode();
        var youtubeResult = await youtube.Content.ReadFromJsonAsync<GenerationResult>();
        Assert.True(youtubeResult!.Success, youtubeResult.Error);

        var title = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/artifacts/{youtubeResult.ArtifactId}/youtube-titles/B/regenerate",
            new YoutubeTitleRegenerationRequest("Use the measured result"));
        title.EnsureSuccessStatusCode();

        var social = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/generate/social-x",
            new { brief = "Use approved evidence" });
        social.EnsureSuccessStatusCode();
        var socialResult = await social.Content.ReadFromJsonAsync<GenerationResult>();
        Assert.True(socialResult!.Success, socialResult.Error);

        var edit = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/artifacts/{socialResult.ArtifactId}/tech-edit",
            new { steering = "Be more specific", useKnowledgeBase = false });
        edit.EnsureSuccessStatusCode();
        var editResult = await edit.Content.ReadFromJsonAsync<TechEditResult>();
        Assert.True(editResult!.Success, editResult.Error);

        var clip = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/generate/clip-suggestions",
            new { brief = "Find a clip" });
        clip.EnsureSuccessStatusCode();
        var clipResult = await clip.Content.ReadFromJsonAsync<GenerationResult>();
        Assert.False(clipResult!.Success);
        Assert.Contains("transcript", clipResult.Error!, StringComparison.OrdinalIgnoreCase);

        var previews = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaignId}/artifacts");
        foreach (var citation in previews!
            .Where(item => item.Kind is "youtube" or "social-x")
            .SelectMany(item => item.Citations ?? []))
        {
            Assert.True(CitationReferenceCodec.TryParse(citation, out var reference));
            Assert.Equal(source.Id, reference.SourceAssetId);
        }
    }

    [Fact]
    public async Task Fully_excluded_approved_source_does_not_fall_back_to_raw_text()
    {
        await using var app = WithFakeModel();
        var (client, campaignId) = await SetUpCampaignWithoutTranscriptAsync(app);
        var sourceArtifact = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/artifacts",
            new ArtifactCreateRequest(
                "campaign-summary",
                "Excluded proof",
                """{"summary":"This raw text must not return after exclusion."}"""));
        sourceArtifact.EnsureSuccessStatusCode();
        var sourceArtifactBody = (await sourceArtifact.Content.ReadFromJsonAsync<ArtifactResponse>())!;
        var imported = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/sources/import/artifact",
            new ArtifactSourceImportRequest(sourceArtifactBody.Id));
        imported.EnsureSuccessStatusCode();
        var revision = (await imported.Content.ReadFromJsonAsync<EvidenceRevisionResponse>())!;
        var block = Assert.Single(revision.Blocks);
        var exclude = await client.PatchAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/sources/{revision.Source.Id}/evidence/{block.StableId}",
            new EvidenceBlockRevisionRequest(null, true));
        exclude.EnsureSuccessStatusCode();
        var draft = (await exclude.Content.ReadFromJsonAsync<EvidenceRevisionResponse>())!;
        var approve = await client.PostAsync(
            $"/api/v1/campaigns/{campaignId}/sources/{revision.Source.Id}/evidence/{draft.Revision}/approve",
            null);
        approve.EnsureSuccessStatusCode();
        var approved = await approve.Content.ReadFromJsonAsync<EvidenceRevisionResponse>();
        Assert.Empty(approved!.Blocks);

        var generation = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/generate/social-x",
            new { brief = "Use approved evidence" });
        generation.EnsureSuccessStatusCode();
        var result = await generation.Content.ReadFromJsonAsync<GenerationResult>();
        Assert.False(result!.Success);
        Assert.Contains("unknown approved evidence", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_count_over_the_cap_is_clamped_rather_than_honoured()
    {
        await using var app = WithFakeModel();
        var (client, campaignId, transcriptId) = await SetUpAsync(app);

        var response = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate",
            new { transcriptArtifactId = transcriptId, kinds = new[] { "newsletter" }, count = 500 });

        // Rejected at the boundary by the Range attribute, never reaching the model.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Prompt_log_records_calls_for_the_current_user_only()
    {
        await using var app = WithFakeModel();
        var (client, campaignId, transcriptId) = await SetUpAsync(app);

        await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate/social-x",
            new { transcriptArtifactId = transcriptId });

        var log = await client.GetFromJsonAsync<List<LogEntry>>("/api/v1/ai/log");
        Assert.Contains(log!, e => e.Kind == "social-x" && e.Success);

        // A different user sees an empty log.
        var (stranger, _, _) = await SetUpAsync(app);
        var strangerLog = await stranger.GetFromJsonAsync<List<LogEntry>>("/api/v1/ai/log");
        Assert.DoesNotContain(strangerLog!, e => e.Kind == "social-x");
    }

    [Fact]
    public async Task Status_reports_none_when_no_credentials_configured()
    {
        // Real factory, empty Ai config: credentialSource must be "none".
        var (client, _, _) = await SetUpAsync(factory);
        var status = await client.GetFromJsonAsync<Castmill.Core.Ai.AiStatusResponse>("/api/v1/ai/status");
        Assert.Equal("none", status!.CredentialSource);
        Assert.False(status.EndpointConfigured);
    }

    [Fact]
    public async Task Generate_without_credentials_reports_failures_not_500s()
    {
        var (client, campaignId, transcriptId) = await SetUpAsync(factory);
        var response = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate/social-x",
            new { transcriptArtifactId = transcriptId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Castmill.Core.Ai.GenerationResult>();
        Assert.False(result!.Success);
        Assert.Contains("Foundry", result.Error, StringComparison.Ordinal);
    }

    private sealed record IngestResponse(Guid TranscriptArtifactId, int SegmentCount);
    private sealed record FanOutResponse(int Succeeded, int Failed);
    private sealed record LogEntry(string Kind, bool Success);

    private static async Task<(HttpClient Client, Guid CampaignId)> SetUpCampaignWithoutTranscriptAsync(
        WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(
                $"evidence-only-{Guid.NewGuid():N}@example.com",
                "correct-horse-battery-staple",
                "Evidence-only tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        var campaign = await client.PostAsJsonAsync(
            "/api/v1/campaigns", new CampaignCreateRequest("Evidence-only campaign", null));
        campaign.EnsureSuccessStatusCode();
        return (client, (await campaign.Content.ReadFromJsonAsync<CampaignResponse>())!.Id);
    }

    // ---- Fakes ---------------------------------------------------------------

    internal sealed class FakeFoundryFactory(bool malformedYoutubeTaxonomy = false) : IFoundryClientFactory
    {
        public Task<FoundryCredentials?> ResolveCredentialsAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<FoundryCredentials?>(new FoundryCredentials("https://fake.local", "fake", "config"));

        public string? ResolveDeployment(string modelAlias) => "fake-deployment";

        public Task<FoundryTarget?> ResolveTargetAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<FoundryTarget?>(new FoundryTarget(
                new FoundryCredentials("https://fake.local", "fake", "config"), "fake-deployment"));

        public Task<IChatClient> CreateChatClientAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<IChatClient>(new FakeChatClient(malformedYoutubeTaxonomy));
    }

    private sealed class PromptEvidenceFoundryFactory : IFoundryClientFactory
    {
        public Task<FoundryCredentials?> ResolveCredentialsAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<FoundryCredentials?>(
                new FoundryCredentials("https://fake.local", "fake", "config"));

        public string? ResolveDeployment(string modelAlias) => "fake-deployment";

        public Task<FoundryTarget?> ResolveTargetAsync(
            Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<FoundryTarget?>(new FoundryTarget(
                new FoundryCredentials("https://fake.local", "fake", "config"),
                "fake-deployment"));

        public Task<IChatClient> CreateChatClientAsync(
            Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<IChatClient>(new PromptEvidenceChatClient());
    }

    private sealed class TitleEvidenceFoundryFactory : IFoundryClientFactory
    {
        public Task<FoundryCredentials?> ResolveCredentialsAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<FoundryCredentials?>(
                new FoundryCredentials("https://fake.local", "fake", "config"));

        public string? ResolveDeployment(string modelAlias) => "fake-deployment";

        public Task<FoundryTarget?> ResolveTargetAsync(
            Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<FoundryTarget?>(new FoundryTarget(
                new FoundryCredentials("https://fake.local", "fake", "config"),
                "fake-deployment"));

        public Task<IChatClient> CreateChatClientAsync(
            Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<IChatClient>(new TitleEvidenceChatClient());
    }

    internal sealed class GeneralizedEvidenceFoundryFactory : IFoundryClientFactory
    {
        public Task<FoundryCredentials?> ResolveCredentialsAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<FoundryCredentials?>(
                new FoundryCredentials("https://fake.local", "fake", "config"));

        public string? ResolveDeployment(string modelAlias) => "fake-deployment";

        public Task<FoundryTarget?> ResolveTargetAsync(
            Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<FoundryTarget?>(new FoundryTarget(
                new FoundryCredentials("https://fake.local", "fake", "config"),
                "fake-deployment"));

        public Task<IChatClient> CreateChatClientAsync(
            Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<IChatClient>(new GeneralizedEvidenceChatClient());
    }

    private sealed class GeneralizedEvidenceChatClient : IChatClient
    {
        private readonly FakeChatClient _fallback = new();

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var materialized = messages.ToList();
            var prompt = string.Join("\n", materialized.Select(message => message.Text));
            var citation = prompt.Split('\n')
                .Where(line => line.StartsWith("Citation ID: ", StringComparison.Ordinal))
                .Select(line => line["Citation ID: ".Length..].Trim())
                .FirstOrDefault();
            if (prompt.Contains("technical editor on this artifact", StringComparison.Ordinal))
            {
                if (citation is null)
                {
                    throw new InvalidOperationException(
                        "The generalized tech-edit prompt needs an approved citation ID.");
                }
                var edited = JsonSerializer.Serialize(new
                {
                    artifact = new
                    {
                        title = "Edited measured rollout",
                        text = "The measured rollout reduced recovery time by forty percent.",
                        hashtags = Array.Empty<string>(),
                        citations = new[] { citation },
                    },
                    changes = new[]
                    {
                        new { what = "Added the measured result", why = "Approved evidence", sourceUrl = "" },
                    },
                });
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, edited));
            }

            var response = await _fallback.GetResponseAsync(
                materialized, options, cancellationToken);
            var root = JsonNode.Parse(response.Text)?.AsObject()
                ?? throw new JsonException("Fake response was not an object.");
            if (root.ContainsKey("citations") && citation is not null)
            {
                root["citations"] = new JsonArray(citation);
            }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, root.ToJsonString()));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => _fallback.Dispose();
    }

    private sealed class TitleEvidenceChatClient : IChatClient
    {
        private readonly FakeChatClient _fallback = new();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var materialized = messages.ToList();
            var prompt = string.Join("\n", materialized.Select(message => message.Text));
            if (!prompt.Contains("Regenerate only title slot", StringComparison.Ordinal))
            {
                return _fallback.GetResponseAsync(materialized, options, cancellationToken);
            }

            var citation = prompt.Split('\n')
                .Where(line => line.StartsWith("Citation ID: ", StringComparison.Ordinal))
                .Select(line => line["Citation ID: ".Length..].Trim())
                .Last();
            var json = JsonSerializer.Serialize(new
            {
                slot = "B",
                title = "New Evidence, Stronger Deployment Title",
                angle = "curiosity",
                score = 91,
                rationale = "Uses newly approved evidence.",
                citations = new[] { citation },
            });
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => _fallback.Dispose();
    }

    private sealed class PromptEvidenceChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var prompt = string.Join("\n", messages.Select(message => message.Text));
            var citation = prompt.Split('\n')
                .Where(line => line.StartsWith("Citation ID: ", StringComparison.Ordinal))
                .Select(line => line["Citation ID: ".Length..].Trim())
                .Last();
            var json = JsonSerializer.Serialize(new
            {
                title = "Post",
                text = "A post grounded in the second approved source.",
                hashtags = Array.Empty<string>(),
                citations = new[] { citation },
            });
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Returns schema-valid canned JSON keyed off distinctive prompt text.</summary>
    internal sealed class FakeChatClient(bool malformedYoutubeTaxonomy = false) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Join("\n", messages.Select(m => m.Text));
            var json = Respond(prompt);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }

        private string Respond(string prompt)
        {
            if (prompt.Contains("Regenerate only title slot", StringComparison.Ordinal))
            {
                return """{"slot":"B","title":"What Halved Our Deployment Time?","angle":"curiosity","score":89,"rationale":"A concrete knowledge gap tied to the source result.","citations":["S2"]}""";
            }
            if (prompt.Contains("Infer the specific audience an SEO/AEO analyst should research", StringComparison.Ordinal))
            {
                return """{"audience":"Platform engineering leaders evaluating deployment automation with measurable recovery-time goals"}""";
            }
            if (prompt.Contains("Plan a YouTube package", StringComparison.Ordinal))
            {
                return """{"searchIntent":"deployment automation","targetKeyword":"deployment automation","hook":"Cut deployment time in half with a grounded automation workflow.","chapters":[{"startSeconds":0,"keyword":"deployment automation","purpose":"answer"},{"startSeconds":8,"keyword":"delivery dashboard","purpose":"proof"},{"startSeconds":16,"keyword":"shipping workflow","purpose":"steps"}],"pinnedCommentMoment":"The deployment time result","titleAngles":[{"slot":"A","angle":"seo","promise":"half the time"},{"slot":"B","angle":"curiosity","promise":"the workflow"},{"slot":"C","angle":"problem-solution","promise":"slow deploys"}],"citations":["S1","S2"]}""";
            }
            if (prompt.Contains("fill out a campaign brief", StringComparison.Ordinal))
            {
                return """{"title":"Faster deployment automation","audience":"Platform engineering leaders evaluating deployment tooling","angle":"The launch cut deployment time in half","summary":"The product launch demonstrates a practical deployment automation workflow. The dashboard makes the improvement visible. The team shipped the change in six weeks.","keyPoints":["Deployment time was cut in half","The dashboard provides concrete proof","The team shipped in six weeks"]}""";
            }
            if (prompt.Contains("Create an outline", StringComparison.Ordinal))
            {
                return """{"title":"Launch story","sections":[{"heading":"Intro","segmentIds":["S1"]}],"citations":["S1","S2"]}""";
            }
            if (prompt.Contains("Write the full blog post", StringComparison.Ordinal))
            {
                var words = string.Join(" ", Enumerable.Repeat("word", 1800));
                return $$"""{"title":"Launch story","markdown":"{{words}} ![stub:blog-hero]()","metaDescription":"d","citations":["S1","S2","S3"]}""";
            }
            if (prompt.Contains("auditing a blog draft", StringComparison.Ordinal))
            {
                return """{"unsupportedClaims":[],"citations":["S1"]}""";
            }
            if (prompt.Contains("nurture sequence", StringComparison.Ordinal))
            {
                return """{"title":"Emails","emails":[{"subject":"a","preview":"p","bodyMarkdown":"Watch: [YOUTUBE_VIDEO_URL]"},{"subject":"b","preview":"p","bodyMarkdown":"b"},{"subject":"c","preview":"p","bodyMarkdown":"b"}],"citations":["S1"]}""";
            }
            if (prompt.Contains("newsletter edition", StringComparison.Ordinal))
            {
                return """{"title":"News","subject":"s","bodyMarkdown":"Watch: [YOUTUBE_VIDEO_URL]","citations":["S2"]}""";
            }
            if (prompt.Contains("landing page copy", StringComparison.Ordinal))
            {
                return """{"title":"Landing","headline":"h","subheadline":"s","sectionsMarkdown":["m"],"cta":"go","citations":["S1"]}""";
            }
            // Keyed on the schema field, not on prose: matching prose is how this fake went
            // stale before, when a prompt was reworded and the generator silently fell through.
            if (prompt.Contains("\"titleOptions\"", StringComparison.Ordinal))
            {
                var package = JsonNode.Parse("""{"title":"Deployment Automation Cuts Delivery Time","titleOptions":[{"slot":"A","title":"Deployment Automation Cuts Delivery Time","angle":"seo","score":91,"rationale":"Leads with the measured result."},{"slot":"B","title":"The Deployment Workflow Behind Faster Shipping","angle":"curiosity","score":84,"rationale":"Opens a useful knowledge gap."},{"slot":"C","title":"Slow Deployments? Fix the Delivery Workflow","angle":"problem-solution","score":82,"rationale":"Names the pain directly."}],"description":"Deployment automation cut delivery time in half, and this grounded workflow shows the exact product and dashboard proof.\n\nLearn how the team shipped the product and used its dashboard.\n\nChapters:\n0:00 Deployment automation result\n0:08 Delivery dashboard proof\n0:16 Faster shipping workflow\n\n{{LINKS}}\n#devops #automation","chapters":[{"startSeconds":0,"title":"Deployment automation result"},{"startSeconds":8,"title":"Delivery dashboard proof"},{"startSeconds":16,"title":"Faster shipping workflow"}],"tags":["deployment automation","devops dashboard","shipping workflow","delivery time","product launch","faster deploys","release process","automation tool"],"suggestedPinnedComment":"The source says deployment time was cut in half after the launch—where would this workflow remove the most delay for your team?","audit":{"hookWithin125":true,"hashtagsHoisted":true,"chapterKeywordsPresent":true,"warnings":[]},"citations":["S1","S2","S3"]}""")!.AsObject();
                if (malformedYoutubeTaxonomy)
                {
                    var options = package["titleOptions"]!.AsArray();
                    options[0]!["angle"] = "search engine optimization";
                    options[2]!["angle"] = "curiosity";
                }
                return package.ToJsonString();
            }
            if (prompt.Contains("show notes", StringComparison.Ordinal))
            {
                return """{"title":"Notes","summaryMarkdown":"s","chapters":[{"startSeconds":0,"title":"Intro"}],"citations":["S1"]}""";
            }
            // Keyed off the schema's field name, not the prose: the clip prompt's wording has
            // already been rewritten once ("short vertical clips" → "vertical short-form
            // clips"), which silently dropped this branch and sank the kind in the fan-out.
            // Field names are the contract, so they are the stable thing to match on.
            if (prompt.Contains("\"platformFit\"", StringComparison.Ordinal))
            {
                // Segment ids, not timestamps — the generator computes in/out from the
                // transcript now, so a fake that returns times would exercise a path the
                // real model no longer takes.
                return """{"title":"Clips","clips":[{"startSegmentId":"S1","endSegmentId":"S3","hook":"h","clipTitle":"Deploy time, halved","description":"d","hashtags":["devops"],"platformFit":["tiktok"],"scores":{"hook":8,"selfContained":7,"payoff":8,"emotion":6}}],"citations":["S2"]}""";
            }
            if (prompt.Contains("Produce an SEO brief", StringComparison.Ordinal))
            {
                return """{"title":"SEO brief","summary":"A launch story about cutting deployment time in half with the new product and dashboard.","focusKeywords":["deployment automation tool","cut deployment time","devops dashboard"],"youtubeTitles":["We Cut Deploy Time in HALF — Here's How","The Dashboard That Halved Our Deployments","Deployment Automation That Actually Works"],"citations":["S2"]}""";
            }
            if (prompt.Contains("image-generation prompts", StringComparison.Ordinal))
            {
                return """{"title":"Images","images":[{"slot":"blog-hero","prompt":"p","aspectRatio":"16:9"},{"slot":"youtube-thumbnail","prompt":"p","aspectRatio":"16:9"},{"slot":"blog-inline-1","prompt":"p","aspectRatio":"4:3"}],"citations":["S3"]}""";
            }
            if (prompt.Contains("\"overlayText\"", StringComparison.Ordinal))
            {
                return """{"title":"Thumbnail directions","concepts":[{"name":"Speed","angle":"faster delivery","prompt":"a fast product launch with negative space","overlayText":"SHIP FASTER","reason":"Matches the deployment intent"},{"name":"Before and after","angle":"transformation","prompt":"split-screen delivery workflow","overlayText":"TIME CUT IN HALF","reason":"Shows the concrete outcome"},{"name":"Dashboard proof","angle":"product evidence","prompt":"product dashboard in an editorial composition","overlayText":"THE PROOF","reason":"Grounds the claim in the product"}],"citations":["S2","S3"]}""";
            }
            // Social posts (each platform prompt says "Write one <platform> post").
            return """{"title":"Post","text":"Short launch post.","hashtags":["#launch"],"citations":["S1"]}""";
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
