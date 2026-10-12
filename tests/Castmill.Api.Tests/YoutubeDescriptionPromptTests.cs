using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Castmill.Api.Services.Ai;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Castmill.Api.Tests;

/// <summary>
/// ADR-087. A producer compared the app's YouTube description with the same model prompted by
/// hand and found the app's forced the search phrase into the title, the opening and a
/// heading, wrote the SEO questions out as an FAQ, and covered four demonstrations of a dozen.
/// The causes were in the prompt: blog answer-engine rules on a video, keyword mandates placed
/// after (so above) the brand's template, a rewrite-everything audit, and a transcript sent as
/// a thousand citation records.
/// </summary>
public sealed class YoutubeDescriptionPromptTests
{
    private static readonly Guid Source = Guid.Parse("6ef61758-a7e1-4129-b310-0cc64389b6ad");

    [Fact]
    public void A_recording_reads_as_timestamped_paragraphs_with_every_block_id_inline()
    {
        var context = Timed(("s01", 0, "Welcome to the webinar."), ("s02", 11.9, "Today we embed dashboards."),
            ("s03", 75, "Now the live demo."), ("s04", 3725, "Questions from the chat."));

        var prompt = context.ToPromptText();

        Assert.Contains("Source: Webinar.mp4 — timed transcript", prompt, StringComparison.Ordinal);
        Assert.Contains($"Cite a passage as evidence:{Source:N}:<id>", prompt, StringComparison.Ordinal);
        Assert.Contains("[s01 0:00] Welcome to the webinar. [s02 0:11] Today we embed dashboards.", prompt, StringComparison.Ordinal);
        // A new paragraph roughly every minute of talk.
        Assert.Contains("dashboards.\n[s03 1:15] Now the live demo.", prompt.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("[s04 1:02:05] Questions from the chat.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Locator:", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("evidence:6ef61758a7e14129b3100cc64389b6ad:s03")]
    [InlineData("s03")]
    public void A_citation_written_from_the_compact_transcript_resolves_to_its_block(string citation)
    {
        var context = Timed(("s01", 0, "Welcome."), ("s03", 75, "Now the live demo."));

        Assert.True(context.TryNormalizeCitations(
            JsonDocument.Parse($$"""{"citations":["{{citation}}"]}""").RootElement.Clone(),
            out var normalized, out var error), error);
        Assert.Equal(CitationReferenceCodec.Format(Source, "s03"), normalized.GetProperty("citations")[0].GetString());
    }

    [Fact]
    public void The_legacy_transcript_path_renders_compactly_with_its_timings()
    {
        var context = GenerationEvidenceContext.FromTranscript(new TranscriptContent("Recording.mp4",
        [
            new TranscriptSegment("s1", 0, 4, null, "Welcome."),
            new TranscriptSegment("s2", 95, 99, null, "The demo."),
        ]));

        var prompt = context.ToPromptText();

        Assert.Contains("Cite a passage by the bracketed id that precedes it.", prompt, StringComparison.Ordinal);
        Assert.Contains("[s1 0:00] Welcome.", prompt, StringComparison.Ordinal);
        Assert.Contains("[s2 1:35] The demo.", prompt, StringComparison.Ordinal);
        Assert.True(context.TryNormalizeCitations(
            JsonDocument.Parse("""{"citations":["s2"]}""").RootElement.Clone(), out _, out var error), error);
    }

    [Fact]
    public void Document_evidence_keeps_one_record_per_block()
    {
        var context = new GenerationEvidenceContext(
            new TranscriptContent("doc", [new TranscriptSegment("p1", 0, 1, null, "Para.")]),
            [new GenerationEvidenceBlock(Source, "Guide.pdf", "p1", "A paragraph.", EvidenceLocatorKinds.DocumentSection, "{}")]);

        var prompt = context.ToPromptText();

        Assert.Contains($"Citation ID: evidence:{Source:N}:p1", prompt, StringComparison.Ordinal);
        Assert.Contains("Content: A paragraph.", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Intro.\n\nFAQ\nWhat is it?\nIt is a thing.", true)]
    [InlineData("Intro.\n\nWhat is embedded analytics?\nIt is...\n\nCan you give an example?\nYes.", true)]
    [InlineData("Intro.\n\n• Will it scale?\n- Does it theme?\n\nWhich part will you try first?", false)]
    [InlineData("A plain description with one closing question?", false)]
    public void Question_sections_in_a_description_are_detected(string description, bool expected) =>
        Assert.Equal(expected, Generators.HasQuestionSections(description));

    [Fact]
    public void The_video_rendering_of_the_research_is_guidance_never_a_mandate()
    {
        var block = BrandContextService.BuildVideoSeoBlock(
            new SeoTargetsResponse("AI-native embedded analytics examples",
                [new SeoTarget("embedded analytics", 1000, 30, 5, "provider")],
                [new SeoQuestion("What is embedded analytics?", "paa")]),
            reportJson: null)!;

        Assert.Contains("guidance, never a checklist", block, StringComparison.Ordinal);
        Assert.Contains("say so in audit.warnings", block, StringComparison.Ordinal);
        Assert.Contains("Never write them out as", block, StringComparison.Ordinal);
        Assert.DoesNotContain("must appear", block, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Answer these questions explicitly", block, StringComparison.Ordinal);
    }

    private static GenerationEvidenceContext Timed(params (string Id, double Start, string Text)[] segments) =>
        new(
            new TranscriptContent("Webinar.mp4", [.. segments.Select(s => new TranscriptSegment(s.Id, s.Start, s.Start + 3, null, s.Text))]),
            [.. segments.Select(s => new GenerationEvidenceBlock(
                Source, "Webinar.mp4", s.Id, s.Text, EvidenceLocatorKinds.MediaTimeRange,
                $$"""{"startSeconds":{{s.Start.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"endSeconds":{{(s.Start + 3).ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"""))]);
}

/// <summary>The prompts the real pipeline sends, captured end to end through the API.</summary>
[Collection("api")]
public sealed class YoutubeDescriptionPipelineTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task A_youtube_package_gets_video_rules_and_research_as_guidance_while_a_blog_keeps_its_targets()
    {
        var capture = new BrandDomainTests.CapturingFoundryFactory();
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(_ => capture))));
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"yt-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "YouTube Tester"));
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        var campaign = await client.PostAsJsonAsync("/api/v1/campaigns", new CampaignCreateRequest("Video", null));
        var campaignId = (await campaign.Content.ReadFromJsonAsync<CampaignResponse>())!.Id;
        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/transcripts",
            new { text = "The deployment workflow cut delivery time in half and the dashboard proves the result.", source = "test" });
        var transcriptId = (await ingest.Content.ReadFromJsonAsync<Ingest>())!.TranscriptArtifactId;
        (await client.PutAsJsonAsync($"/api/v1/campaigns/{campaignId}/seo-targets", new SeoTargetsRequest(
            "AI-native embedded analytics examples",
            [new SeoTarget("embedded analytics", 1000, 30, 5, "provider")],
            [new SeoQuestion("What is embedded analytics?", "paa")]))).EnsureSuccessStatusCode();

        (await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate/youtube",
            new { transcriptArtifactId = transcriptId })).EnsureSuccessStatusCode();

        var passes = capture.Prompts.Where(p => p.Contains("YouTube package", StringComparison.Ordinal)).ToList();
        Assert.True(passes.Count >= 3, $"{passes.Count} YouTube passes");
        Assert.All(passes, prompt =>
        {
            Assert.Contains("VIDEO DESCRIPTION RULES", prompt, StringComparison.Ordinal);
            Assert.Contains("guidance, never a checklist", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("SEARCH AND ANSWER-ENGINE RULES", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIMARY keyword:", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("in the first 125 characters", prompt, StringComparison.Ordinal);
        });
        var draft = passes.Single(p => p.Contains("Write the complete YouTube package", StringComparison.Ordinal));
        Assert.Contains("DESCRIPTION STRUCTURE", draft, StringComparison.Ordinal);
        Assert.Contains("6-8 action-oriented bullets", draft, StringComparison.Ordinal);
        Assert.Contains("represents the WHOLE video", draft, StringComparison.Ordinal);
        var audit = passes.Single(p => p.Contains("Check this YouTube package", StringComparison.Ordinal));
        Assert.Contains("fix ONLY what fails", audit, StringComparison.Ordinal);
        Assert.Contains("Keep every\nsentence that passes word for word", audit.ReplaceLineEndings("\n"), StringComparison.Ordinal);

        capture.Prompts.Clear();
        (await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate/blog",
            new { transcriptArtifactId = transcriptId })).EnsureSuccessStatusCode();
        Assert.Contains(capture.Prompts, p => p.Contains("PRIMARY keyword: \"AI-native embedded analytics examples\"", StringComparison.Ordinal)
            && p.Contains("SEARCH AND ANSWER-ENGINE RULES", StringComparison.Ordinal));
    }

    private sealed record Ingest(Guid TranscriptArtifactId);
}
