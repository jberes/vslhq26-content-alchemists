using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Knowledge;
using Castmill.Core.Ai;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Castmill.Api.Tests;

/// <summary>
/// The second pass end to end (ADR-020). What separates it from every other AI path is that
/// it revises the artifact <b>in place</b> behind a revision snapshot — same id, next version,
/// restorable — instead of printing a new row. And it is held to the same validator pass 1
/// used, so a bad edit is refused rather than written over a good draft.
/// </summary>
[Collection("api")]
public sealed class TechEditApiTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task A_tech_edit_revises_in_place_and_rings_the_revision_filmstrip()
    {
        await using var app = WithFakeModel(FakeTechEditor.Good);
        var (client, campaignId, artifactId) = await SetUpAsync(app);

        var before = await GetArtifactAsync(client, campaignId, artifactId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/artifacts/{artifactId}/tech-edit",
            new { steering = "Be more specific about the numbers.", useKnowledgeBase = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<TechEditResult>();
        Assert.True(result!.Success, result.Error);

        // Same artifact, next version — not a new row.
        Assert.Equal(artifactId, result.ArtifactId);
        Assert.Equal(before.Version + 1, result.Version);
        Assert.Equal($"\"{result.Version}\"", response.Headers.ETag!.ToString());

        var after = await GetArtifactAsync(client, campaignId, artifactId);
        Assert.Contains("deployment time by 47%", after.ContentJson, StringComparison.Ordinal);

        // The take before the edit is recoverable.
        var revisions = await client.GetFromJsonAsync<List<ArtifactRevisionResponse>>(
            $"/api/v1/campaigns/{campaignId}/artifacts/{artifactId}/revisions");
        Assert.Equal("tech-edit", revisions![0].Reason);

        // The model's own account of what it changed rides out as reviewable warnings.
        Assert.Contains(result.Changes, c => c.Contains("47%", StringComparison.Ordinal));
        Assert.Contains("Tech edit:", after.ContentJson, StringComparison.Ordinal);

        // The campaign still holds exactly one blog: nothing was duplicated.
        var previews = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaignId}/artifacts");
        Assert.Single(previews!, p => p.Kind == "blog");
    }

    /// <summary>
    /// The safety property. A second pass that drops the provenance citations is rejected by
    /// the same validator generation used, and the good draft on disk is left untouched.
    /// </summary>
    [Fact]
    public async Task An_edit_that_fails_validation_never_overwrites_the_draft()
    {
        await using var app = WithFakeModel(FakeTechEditor.DropsCitations);
        var (client, campaignId, artifactId) = await SetUpAsync(app);

        var before = await GetArtifactAsync(client, campaignId, artifactId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/artifacts/{artifactId}/tech-edit",
            new { steering = (string?)null, useKnowledgeBase = false });

        var result = await response.Content.ReadFromJsonAsync<TechEditResult>();
        Assert.False(result!.Success);
        Assert.Contains("validation", result.Error!, StringComparison.OrdinalIgnoreCase);

        var after = await GetArtifactAsync(client, campaignId, artifactId);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.ContentJson, after.ContentJson);

        var revisions = await client.GetFromJsonAsync<List<ArtifactRevisionResponse>>(
            $"/api/v1/campaigns/{campaignId}/artifacts/{artifactId}/revisions");
        Assert.Empty(revisions!);
    }

    /// <summary>A transcript is source material; editing it would corrupt every citation.</summary>
    [Fact]
    public async Task A_transcript_cannot_be_tech_edited()
    {
        await using var app = WithFakeModel(FakeTechEditor.Good);
        var (client, campaignId, _) = await SetUpAsync(app);

        var previews = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaignId}/artifacts");
        var transcriptId = previews!.Single(p => p.Kind == "transcript").Id;

        var response = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/artifacts/{transcriptId}/tech-edit",
            new { steering = (string?)null, useKnowledgeBase = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The knowledge base is optional and best-effort: with no gateway configured the pass
    /// still runs, and simply reports that it did not consult one.
    /// </summary>
    [Fact]
    public async Task Without_a_knowledge_gateway_the_pass_still_runs_and_says_it_used_none()
    {
        await using var app = WithFakeModel(FakeTechEditor.Good);
        var (client, campaignId, artifactId) = await SetUpAsync(app);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/artifacts/{artifactId}/tech-edit",
            new { steering = (string?)null, useKnowledgeBase = true });

        var result = await response.Content.ReadFromJsonAsync<TechEditResult>();
        Assert.True(result!.Success, result.Error);
        Assert.False(result.KnowledgeBaseUsed);
    }

    // ---- setup -----------------------------------------------------------------

    private WebApplicationFactory<Program> WithFakeModel(Func<string, string> respond) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(
                _ => new FakeFactory(respond)))));

    /// <summary>Creates a campaign with a transcript and one validated blog to edit.</summary>
    private static async Task<(HttpClient Client, Guid CampaignId, Guid ArtifactId)> SetUpAsync(
        WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"tech-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Editor"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var campaign = await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Tech edit campaign", null));
        var campaignId = (await campaign.Content.ReadFromJsonAsync<CampaignResponse>())!.Id;

        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/transcripts",
            new
            {
                text = "We launched the new product. It cut deployment time in half. "
                     + "Customers love the new dashboard. The team shipped it in six weeks.",
                source = "unit-test",
            });
        ingest.EnsureSuccessStatusCode();
        var transcriptId = (await ingest.Content.ReadFromJsonAsync<IngestResponse>())!.TranscriptArtifactId;

        var generate = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/generate/blog",
            new { transcriptArtifactId = transcriptId });
        var blog = await generate.Content.ReadFromJsonAsync<GenerationResult>();
        Assert.True(blog!.Success, blog.Error);

        return (client, campaignId, blog.ArtifactId!.Value);
    }

    private static async Task<ArtifactResponse> GetArtifactAsync(HttpClient client, Guid campaignId, Guid id) =>
        (await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaignId}/artifacts/{id}"))!;

    private sealed record IngestResponse(Guid TranscriptArtifactId, int SegmentCount);

    // ---- fakes -----------------------------------------------------------------

    /// <summary>Canned second-pass responses, keyed off the editor instruction.</summary>
    private static class FakeTechEditor
    {
        private const string Words = "word word word word word";

        public static string Good(string prompt) =>
            IsTechEdit(prompt)
                ? $$"""
                    {"artifact":{"title":"Launch story","markdown":"{{Body}} cut deployment time by 47%.",
                     "metaDescription":"d","citations":["S1","S2"]},
                     "changes":[{"what":"Replaced 'in half' with 47%","why":"The docs give the real figure",
                     "sourceUrl":"https://example.test/docs"}]}
                    """
                : Draft(prompt);

        public static string DropsCitations(string prompt) =>
            IsTechEdit(prompt)
                ? $$"""
                    {"artifact":{"title":"Launch story","markdown":"{{Body}}","metaDescription":"d"},
                     "changes":[]}
                    """
                : Draft(prompt);

        private static bool IsTechEdit(string prompt) =>
            prompt.Contains("You are the technical editor", StringComparison.Ordinal);

        private static string Body => string.Join(" ", Enumerable.Repeat(Words, 360));

        /// <summary>The pass-1 blog pipeline, so there is a validated artifact to edit.</summary>
        private static string Draft(string prompt)
        {
            if (prompt.Contains("Create an outline", StringComparison.Ordinal))
            {
                return """{"title":"Launch story","sections":[{"heading":"Intro","segmentIds":["S1"]}],"citations":["S1"]}""";
            }
            if (prompt.Contains("auditing a blog draft", StringComparison.Ordinal))
            {
                return """{"unsupportedClaims":[],"citations":["S1"]}""";
            }
            return $$"""
                {"title":"Launch story","markdown":"{{Body}}","metaDescription":"d","citations":["S1","S2"]}
                """;
        }
    }

    private sealed class FakeFactory(Func<string, string> respond) : IFoundryClientFactory
    {
        public Task<FoundryCredentials?> ResolveCredentialsAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<FoundryCredentials?>(new FoundryCredentials("https://fake.local", "fake", "config"));

        public string? ResolveDeployment(string modelAlias) => "fake-deployment";

        public Task<FoundryTarget?> ResolveTargetAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<FoundryTarget?>(new FoundryTarget(
                new FoundryCredentials("https://fake.local", "fake", "config"), "fake-deployment"));

        public Task<IChatClient> CreateChatClientAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<IChatClient>(new FakeChatClient(respond));
    }

    private sealed class FakeChatClient(Func<string, string> respond) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Join("\n", messages.Select(m => m.Text));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, respond(prompt))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
