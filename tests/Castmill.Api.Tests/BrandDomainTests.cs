using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Services.Ai;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Castmill.Api.Tests;

/// <summary>
/// The brand kit (item 5/6 of the UX overhaul): typed style card, templates, campaign
/// association + context links, brand-delete detachment, and — the point of it all —
/// brand steering actually reaching the prompt.
/// </summary>
[Collection("api")]
public sealed class BrandDomainTests(CastmillApiFactory factory)
{
    private async Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program>? app = null)
    {
        var client = (app ?? factory).CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"brand-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Brand Tester"));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    private static async Task<BrandProfileDetailResponse> CreateBrandAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/brands", new BrandProfileUpsertRequest(
            "Acme Robotics",
            new BrandStyleCard(
                Voice: "Direct, engineer-to-engineer, no hype",
                Audience: "Platform teams",
                Colors: [new BrandColor("primary", "#0A66C2")],
                ImageStyle: "Clean editorial photography, muted blues",
                BannedPhrases: ["game-changer"])));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!;
    }

    [Fact]
    public async Task Style_card_roundtrips_typed_and_rejects_bad_hex()
    {
        var client = await AuthedClientAsync();
        var brand = await CreateBrandAsync(client);

        Assert.Equal("Direct, engineer-to-engineer, no hype", brand.StyleCard?.Voice);
        Assert.Equal("#0A66C2", Assert.Single(brand.StyleCard!.Colors!).Hex);

        var bad = await client.PutAsJsonAsync($"/api/v1/brands/{brand.Id}", new BrandProfileUpsertRequest(
            "Acme", new BrandStyleCard(Colors: [new BrandColor("primary", "not-a-hex")])));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Templates_validate_kind_and_keep_one_default_per_kind()
    {
        var client = await AuthedClientAsync();
        var brand = await CreateBrandAsync(client);
        var baseUrl = $"/api/v1/brands/{brand.Id}/templates";

        var unknown = await client.PostAsJsonAsync(baseUrl,
            new BrandTemplateRequest("press-release", "Nope", "steer"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // Image-planning prompts are internal generator machinery, not Brand content types.
        var operational = await client.PostAsJsonAsync(baseUrl,
            new BrandTemplateRequest("image-prompts", "Hidden machinery", "steer"));
        Assert.Equal(HttpStatusCode.BadRequest, operational.StatusCode);

        var first = await client.PostAsJsonAsync(baseUrl,
            new BrandTemplateRequest("newsletter", "Monthly", "Three sections, one CTA.", IsDefault: true));
        first.EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync(baseUrl,
            new BrandTemplateRequest("newsletter", "Launch special", "Louder.", IsDefault: true));
        second.EnsureSuccessStatusCode();

        // A real strategy prompt is substantially longer than a style hint. YouTube is a
        // first-class kind and the complete prompt must survive validation + persistence.
        var youtubePrompt = "YOUTUBE-PRIMARY-BEGIN\n" + new string('y', 7_600) + "\nYOUTUBE-PRIMARY-END";
        var youtube = await client.PostAsJsonAsync(baseUrl,
            new BrandTemplateRequest("youtube", "YouTube strategy", youtubePrompt, IsDefault: true));
        youtube.EnsureSuccessStatusCode();

        var templates = await client.GetFromJsonAsync<List<BrandTemplateResponse>>(baseUrl);
        Assert.Equal(3, templates!.Count);
        Assert.Equal("Launch special", Assert.Single(templates,
            t => t.Kind == "newsletter" && t.IsDefault).Name);
        Assert.Equal(youtubePrompt, Assert.Single(templates,
            t => t.Kind == "youtube" && t.IsDefault).SteeringPrompt);
    }

    [Fact]
    public async Task Campaign_carries_brand_and_links_and_rejects_a_foreign_brand()
    {
        var client = await AuthedClientAsync();
        var brand = await CreateBrandAsync(client);

        var links = new List<CampaignLink>
        {
            new("Home page", "https://acme.example"),
            new("GitHub", "https://github.com/acme/robots", "the OSS repo"),
        };
        var create = await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Branded campaign", "brief", brand.Id, links));
        create.EnsureSuccessStatusCode();
        var campaign = (await create.Content.ReadFromJsonAsync<CampaignResponse>())!;
        Assert.Equal(brand.Id, campaign.BrandId);
        Assert.Equal(2, campaign.Links!.Count);

        // The preview carries the brand summary for the header chip.
        var preview = await client.GetAsync($"/api/v1/campaigns/{campaign.Id}/preview");
        Assert.Contains("Acme Robotics", await preview.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Another tenant's brand id is a plain 400 — the filter hides its existence.
        var stranger = await AuthedClientAsync();
        var foreign = await stranger.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Sneaky", null, brand.Id, null));
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_brand_detaches_campaigns_and_removes_the_kit()
    {
        var client = await AuthedClientAsync();
        var brand = await CreateBrandAsync(client);

        var create = await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Attached", null, brand.Id, null));
        var campaign = (await create.Content.ReadFromJsonAsync<CampaignResponse>())!;

        (await client.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/templates",
            new BrandTemplateRequest("blog", "Voice", "steer", IsDefault: true))).EnsureSuccessStatusCode();

        var delete = await client.DeleteAsync($"/api/v1/brands/{brand.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var reloaded = await client.GetFromJsonAsync<CampaignResponse>($"/api/v1/campaigns/{campaign.Id}");
        Assert.Null(reloaded!.BrandId);
    }

    /// <summary>
    /// The label is the prompt text, so renaming is a content decision — and it must not
    /// require re-uploading the file it describes. Blank clears back to the filename.
    /// </summary>
    [Fact]
    public async Task An_asset_label_renames_in_place_and_blank_clears_it()
    {
        var client = await AuthedClientAsync();
        var brand = await CreateBrandAsync(client);

        var asset = await (await client.PostAsJsonAsync("/api/v1/assets",
            new AssetCreateRequest("face.png", "image/png", 100))).Content
            .ReadFromJsonAsync<AssetResponse>();
        var link = await (await client.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/assets",
            new BrandAssetLinkRequest(asset!.Id, "face", "old label"))).Content
            .ReadFromJsonAsync<BrandAssetResponse>();

        var rename = await client.PatchAsJsonAsync(
            $"/api/v1/brands/{brand.Id}/assets/{link!.Id}",
            new BrandAssetLabelRequest("the host, short dark hair"));
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);

        var kit = await client.GetFromJsonAsync<List<BrandAssetResponse>>($"/api/v1/brands/{brand.Id}/assets");
        Assert.Equal("the host, short dark hair", Assert.Single(kit!).Label);

        (await client.PatchAsJsonAsync($"/api/v1/brands/{brand.Id}/assets/{link.Id}",
            new BrandAssetLabelRequest("   "))).EnsureSuccessStatusCode();
        kit = await client.GetFromJsonAsync<List<BrandAssetResponse>>($"/api/v1/brands/{brand.Id}/assets");
        Assert.Null(Assert.Single(kit!).Label);

        var retype = await client.PatchAsJsonAsync(
            $"/api/v1/brands/{brand.Id}/assets/{link.Id}/kind",
            new BrandAssetKindRequest("background"));
        Assert.Equal(HttpStatusCode.OK, retype.StatusCode);
        Assert.Equal("background", (await retype.Content.ReadFromJsonAsync<BrandAssetResponse>())!.Kind);
        kit = await client.GetFromJsonAsync<List<BrandAssetResponse>>($"/api/v1/brands/{brand.Id}/assets");
        Assert.Equal("background", Assert.Single(kit!).Kind);

        var invalidKind = await client.PatchAsJsonAsync(
            $"/api/v1/brands/{brand.Id}/assets/{link.Id}/kind",
            new BrandAssetKindRequest("avatar"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidKind.StatusCode);

        // Another brand's link id is a plain 404 — the tenant filter plus the brand check.
        var foreign = await client.PatchAsJsonAsync(
            $"/api/v1/brands/{brand.Id}/assets/{Guid.NewGuid()}",
            new BrandAssetLabelRequest("x"));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Brand_voice_template_and_context_links_reach_the_generation_prompt()
    {
        var capture = new CapturingFoundryFactory();
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(_ => capture))));

        var client = await AuthedClientAsync(app);
        var brand = await CreateBrandAsync(client);
        (await client.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/templates",
            new BrandTemplateRequest("newsletter", "Monthly", "Exactly three sections and a PS.", IsDefault: true)))
            .EnsureSuccessStatusCode();

        var create = await client.PostAsJsonAsync("/api/v1/campaigns", new CampaignCreateRequest(
            "Steered", null, brand.Id, [new CampaignLink("Home page", "https://acme.example")]));
        var campaignId = (await create.Content.ReadFromJsonAsync<CampaignResponse>())!.Id;

        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/transcripts",
            new { text = "We launched. It cut deploy time in half. Everyone was pleased with the result.", source = "test" });
        ingest.EnsureSuccessStatusCode();
        var transcriptId = (await ingest.Content.ReadFromJsonAsync<IngestResponse>())!.TranscriptArtifactId;

        var generate = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/generate/newsletter",
            new { transcriptArtifactId = transcriptId });
        generate.EnsureSuccessStatusCode();

        var prompt = Assert.Single(capture.Prompts, p => p.Contains("newsletter edition", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Direct, engineer-to-engineer, no hype", prompt, StringComparison.Ordinal);
        Assert.Contains("Exactly three sections and a PS.", prompt, StringComparison.Ordinal);
        Assert.Contains("https://acme.example", prompt, StringComparison.Ordinal);
        Assert.Contains("game-changer", prompt, StringComparison.Ordinal); // banned phrases listed
    }

    [Fact]
    public async Task Youtube_brand_template_is_the_primary_brief_in_all_three_generation_passes()
    {
        var capture = new CapturingFoundryFactory();
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(_ => capture))));

        var client = await AuthedClientAsync(app);
        var brand = await CreateBrandAsync(client);
        const string template = "YOUTUBE-AUTHORITATIVE-BEGIN\nBuild the semantic topic cluster before writing.\nYOUTUBE-AUTHORITATIVE-END";
        (await client.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/templates",
            new BrandTemplateRequest("youtube", "YouTube strategy", template, IsDefault: true)))
            .EnsureSuccessStatusCode();

        var create = await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("YouTube steered", "Technical walkthrough", brand.Id));
        var campaignId = (await create.Content.ReadFromJsonAsync<CampaignResponse>())!.Id;
        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/transcripts",
            new { text = "The deployment workflow cut delivery time in half and the dashboard proves the result.", source = "test" });
        var transcriptId = (await ingest.Content.ReadFromJsonAsync<IngestResponse>())!.TranscriptArtifactId;

        var generate = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/generate/youtube",
            new { transcriptArtifactId = transcriptId });
        generate.EnsureSuccessStatusCode();

        var youtubePasses = capture.Prompts.Where(prompt =>
            prompt.Contains("Plan a YouTube package", StringComparison.Ordinal)
            || prompt.Contains("Write the complete YouTube package", StringComparison.Ordinal)
            || prompt.Contains("Audit and correct this YouTube package", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, youtubePasses.Count);
        Assert.All(youtubePasses, prompt =>
        {
            Assert.Contains("PRIMARY BRAND CONTENT TEMPLATE", prompt, StringComparison.Ordinal);
            Assert.Contains("YOUTUBE-AUTHORITATIVE-END", prompt, StringComparison.Ordinal);
            Assert.Contains("overrides conflicting generic writing guidance", prompt, StringComparison.Ordinal);
            Assert.True(prompt.IndexOf("PRIMARY BRAND CONTENT TEMPLATE", StringComparison.Ordinal)
                < prompt.IndexOf("GENERATOR PASS AND REQUIRED RESPONSE SHAPE", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task A_campaign_without_a_brand_generates_with_no_brand_block()
    {
        var capture = new CapturingFoundryFactory();
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(_ => capture))));

        var client = await AuthedClientAsync(app);
        var create = await client.PostAsJsonAsync("/api/v1/campaigns", new CampaignCreateRequest("Plain", null));
        var campaignId = (await create.Content.ReadFromJsonAsync<CampaignResponse>())!.Id;

        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/transcripts",
            new { text = "We launched. It cut deploy time in half. Everyone was pleased with the result.", source = "test" });
        var transcriptId = (await ingest.Content.ReadFromJsonAsync<IngestResponse>())!.TranscriptArtifactId;

        (await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate/newsletter",
            new { transcriptArtifactId = transcriptId })).EnsureSuccessStatusCode();

        var prompt = Assert.Single(capture.Prompts, p => p.Contains("newsletter edition", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Brand:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Campaign context links", prompt, StringComparison.Ordinal);
    }

    private sealed record IngestResponse(Guid TranscriptArtifactId, int SegmentCount);

    /// <summary>The AiGenerationTests fake, plus prompt capture for injection asserts.</summary>
    private sealed class CapturingFoundryFactory : IFoundryClientFactory
    {
        public List<string> Prompts { get; } = [];

        public Task<FoundryCredentials?> ResolveCredentialsAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<FoundryCredentials?>(new FoundryCredentials("https://fake.local", "fake", "config"));

        public string? ResolveDeployment(string modelAlias) => "fake-deployment";

        public Task<FoundryTarget?> ResolveTargetAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<FoundryTarget?>(new FoundryTarget(
                new FoundryCredentials("https://fake.local", "fake", "config"), "fake-deployment"));

        public Task<IChatClient> CreateChatClientAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<IChatClient>(new CapturingChatClient(Prompts));
    }

    private sealed class CapturingChatClient(List<string> prompts) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Join("\n", messages.Select(m => m.Text));
            prompts.Add(prompt);
            var response = prompt.Contains("Plan a YouTube package", StringComparison.Ordinal)
                ? """{"searchIntent":"deployment automation","targetKeyword":"deployment automation","hook":"Cut deployment time in half.","chapters":[{"startSeconds":0,"keyword":"deployment automation","purpose":"answer"},{"startSeconds":8,"keyword":"delivery dashboard","purpose":"proof"},{"startSeconds":16,"keyword":"shipping workflow","purpose":"steps"}],"pinnedCommentMoment":"The measured result","titleAngles":[{"slot":"A","angle":"seo","promise":"faster delivery"},{"slot":"B","angle":"curiosity","promise":"the workflow"},{"slot":"C","angle":"problem-solution","promise":"slow deploys"}],"citations":["S1"]}"""
                : prompt.Contains("\"titleOptions\"", StringComparison.Ordinal)
                    ? """{"title":"Deployment Automation Cuts Delivery Time","titleOptions":[{"slot":"A","title":"Deployment Automation Cuts Delivery Time","angle":"seo","score":91,"rationale":"Search-led."},{"slot":"B","title":"The Workflow Behind Faster Deployments","angle":"curiosity","score":86,"rationale":"Useful knowledge gap."},{"slot":"C","title":"Slow Deployments? Fix the Workflow","angle":"problem-solution","score":83,"rationale":"Names the problem."}],"description":"Deployment automation cut delivery time in half, and this walkthrough shows the dashboard evidence behind the result.\n\nLearn the grounded workflow.\n\nChapters:\n0:00 Deployment automation\n0:08 Delivery dashboard\n0:16 Shipping workflow\n\n{{LINKS}}","chapters":[{"startSeconds":0,"title":"Deployment automation"},{"startSeconds":8,"title":"Delivery dashboard"},{"startSeconds":16,"title":"Shipping workflow"}],"tags":["deployment automation","delivery workflow","devops dashboard","faster deployment","release automation","shipping workflow","platform engineering","delivery time"],"suggestedPinnedComment":"The walkthrough reports that deployment time was cut in half—which part of this workflow would help your team most?","audit":{"hookWithin125":true,"hashtagsHoisted":true,"chapterKeywordsPresent":true,"warnings":[]},"citations":["S1"]}"""
                    : """{"title":"News","subject":"s","bodyMarkdown":"Watch: [YOUTUBE_VIDEO_URL]","citations":["S1"]}""";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                response)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
