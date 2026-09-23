using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Services.Ai;
using Castmill.Core.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Castmill.Api.Tests;

/// <summary>
/// Stage 2 and 3 of the blog pipeline (ADR-071). The edit stage returns an article, not JSON,
/// because the authored brief asks for Markdown — so the parser, the length floor and the
/// formatting contract are where this stage can go wrong.
/// </summary>
public sealed class BlogEditorTests
{
    [Fact]
    public void The_authored_brief_is_compiled_in_and_carries_its_priorities()
    {
        Assert.Contains("You are the final technical editor", BlogEditor.Brief, StringComparison.Ordinal);
        Assert.Contains("Do not merely critique the draft", BlogEditor.Brief, StringComparison.Ordinal);
        Assert.Contains("CONSOLIDATE ANSWERS", BlogEditor.Brief, StringComparison.Ordinal);
    }

    /// <summary>
    /// The observed corruption: the edit model answered with a JSON envelope although the
    /// brief asks for bare Markdown, and the whole document was persisted as the artifact's
    /// body. Validation cannot see it — a JSON document is a perfectly good non-empty string —
    /// so the parser is the only place it can be caught.
    /// </summary>
    [Theory]
    [InlineData("article")]
    [InlineData("markdown")]
    [InlineData("body")]
    [InlineData("content")]
    [InlineData("text")]
    public void A_json_envelope_is_unwrapped_to_the_article_it_hides(string field)
    {
        var reply = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [field] = "# Real title\n\nReal prose.",
            ["citations"] = new[] { "evidence:abc:s01" },
        });

        var (markdown, notes) = BlogEditor.Parse(reply);

        Assert.Equal("# Real title\n\nReal prose.", markdown);
        Assert.Empty(notes);
    }

    [Fact]
    public void A_tech_edit_envelope_nested_under_artifact_is_unwrapped_too()
    {
        var reply = """{"artifact":{"markdown":"# Title\n\nProse."},"changes":[]}""";

        Assert.Equal("# Title\n\nProse.", BlogEditor.Parse(reply).Markdown);
    }

    /// <summary>
    /// An envelope carrying no prose field must come back empty, which puts the caller under
    /// its publish floor so the unedited draft is kept. Publishing the wrapper is the failure.
    /// </summary>
    [Fact]
    public void An_envelope_with_no_prose_field_yields_nothing_so_the_draft_is_kept()
    {
        Assert.Equal(string.Empty, BlogEditor.Parse("""{"changes":[],"citations":[]}""").Markdown);
    }

    [Fact]
    public void Prose_is_never_mistaken_for_an_envelope()
    {
        // Markdown that merely opens with a brace, and a fenced reply, both survive intact.
        Assert.Equal("{not json} and more", BlogEditor.Parse("{not json} and more").Markdown);
        Assert.Equal("# Heading\n\nBody.", BlogEditor.Parse("# Heading\n\nBody.").Markdown);
    }

    [Fact]
    public void The_prompt_carries_the_draft_and_a_length_floor()
    {
        var prompt = BlogEditor.BuildPrompt("A title", "## Body\nSome prose.", 1400);

        Assert.Contains("You are the final technical editor", prompt, StringComparison.Ordinal);
        Assert.Contains("at least 1400 words", prompt, StringComparison.Ordinal);
        Assert.Contains("Title: A title", prompt, StringComparison.Ordinal);
        Assert.Contains("## Body", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_article_comes_back_whole()
    {
        var (markdown, notes) = BlogEditor.Parse("# Title\n\n## One\nProse.\n");

        Assert.Equal("# Title\n\n## One\nProse.", markdown);
        Assert.Empty(notes);
    }

    [Fact]
    public void Editorial_notes_are_split_off_and_never_published()
    {
        var (markdown, notes) = BlogEditor.Parse(
            "## One\nProse.\n\n## Editorial notes\n- The version number is not in the evidence.\n- No link for the sample.\n");

        Assert.Equal("## One\nProse.", markdown);
        Assert.Equal(
            ["The version number is not in the evidence.", "No link for the sample."],
            notes.ToArray());
        Assert.DoesNotContain("Editorial notes", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("## Editorial notes")]
    [InlineData("### Editorial Notes:")]
    [InlineData("# Editor's notes")]
    [InlineData("**Editorial notes**")]
    [InlineData("## Notes to the editor")]
    [InlineData("## Publication blockers")]
    public void Every_name_a_model_gives_its_notes_is_cut_out_of_the_article(string heading)
    {
        var (markdown, notes) = BlogEditor.StripNotes(
            $"# Title\n\n## One\nProse.\n\n{heading}\n\nThe supplied evidence is a screen recording, not API documentation.\n");

        Assert.Equal("# Title\n\n## One\nProse.", markdown);
        Assert.Equal(["The supplied evidence is a screen recording, not API documentation."], notes.ToArray());
    }

    [Fact]
    public void A_heading_that_merely_mentions_notes_in_the_body_is_left_alone()
    {
        var article = "# Title\n\n## Release notes for 24.2\nWhat shipped.\n\n## Editorial calendar tips\nPlan ahead.";
        var (markdown, notes) = BlogEditor.StripNotes(article);
        Assert.Equal(article, markdown);
        Assert.Empty(notes);
    }

    [Fact]
    public void A_reply_wrapped_in_one_fence_is_unwrapped()
    {
        var (markdown, _) = BlogEditor.Parse("```markdown\n## One\nProse.\n```");
        Assert.Equal("## One\nProse.", markdown);
    }

    [Fact]
    public void A_mention_of_editorial_notes_in_prose_is_not_a_heading()
    {
        var (markdown, notes) = BlogEditor.Parse("## One\nWe keep editorial notes out of the body.\n");

        Assert.Contains("editorial notes", markdown, StringComparison.Ordinal);
        Assert.Empty(notes);
    }
}

/// <summary>
/// The blog's stored title is captured from the DRAFT, but the edit pass rewrites the body
/// including its own H1. Left alone the two drift, and Focus stacks two different titles.
/// </summary>
public sealed class BlogTitleSyncTests
{
    [Fact]
    public void The_articles_own_heading_is_what_the_post_is_called()
    {
        Assert.Equal("Requires Predictable Focus Transitions",
            BlogMarkdown.LeadingHeading("# Requires Predictable Focus Transitions\n\nBody."));
    }

    [Fact]
    public void A_subheading_is_not_the_articles_title()
    {
        Assert.Null(BlogMarkdown.LeadingHeading("## Only a section\n\nBody."));
        Assert.Null(BlogMarkdown.LeadingHeading("Just prose, no heading."));
        Assert.Null(BlogMarkdown.LeadingHeading(""));
    }

    [Fact]
    public void Retitling_the_envelope_leaves_every_other_field_alone()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """{"title":"Old","markdown":"# New\n\nBody.","citations":["evidence:a:s01"]}""");

        var updated = ArtifactContentJson.WithTitle(document.RootElement, "New");

        Assert.Equal("New", updated.GetProperty("title").GetString());
        Assert.Equal("# New\n\nBody.", updated.GetProperty("markdown").GetString());
        Assert.Equal("evidence:a:s01", updated.GetProperty("citations")[0].GetString());
    }
}

/// <summary>
/// A campaign's SEO targets are ONE shared question list, so handing all of it to every blog
/// made every blog answer the same FAQ — duplicate answer-surface content competing with
/// itself. Sibling coverage is read back from the posts' own question headings so it is
/// correct for blogs written before this existed.
/// </summary>
public sealed class BlogQuestionCoverageTests
{
    [Fact]
    public void Question_headings_are_read_from_a_stored_post()
    {
        var content = System.Text.Json.JsonSerializer.Serialize(new
        {
            content = new
            {
                markdown = "# Title\n\n## Not a question\n\nBody.\n\n"
                    + "### What is embedded analytics?\n\nAn answer.\n\n"
                    + "## What are the four types of analytics?\n\nAnother.\n",
            },
        });

        Assert.Equal(
            ["What is embedded analytics?", "What are the four types of analytics?"],
            BlogMarkdown.QuestionHeadings(content));
    }

    [Fact]
    public void A_heading_that_is_not_a_question_is_not_coverage()
    {
        var content = """{"content":{"markdown":"## How it works\n\nBody."}}""";

        Assert.Empty(BlogMarkdown.QuestionHeadings(content));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"content":{}}""")]
    public void Unreadable_content_contributes_no_coverage_rather_than_throwing(string? contentJson)
    {
        // A malformed sibling must not take down the generation that consults it.
        Assert.Empty(BlogMarkdown.QuestionHeadings(contentJson));
    }
}

public sealed class BlogMarkdownTests
{
    [Fact]
    public void A_well_formed_article_has_no_problems()
    {
        const string markdown = """
            Intro prose.

            ## What does it do?

            An answer.

            | Option | Cost |
            | --- | --- |
            | A | low |
            | B | high |

            ```csharp
            var x = 1;
            ```
            """;

        Assert.Empty(BlogMarkdown.Problems(markdown));
    }

    [Fact]
    public void An_unclosed_fence_is_reported()
    {
        var problems = BlogMarkdown.Problems("## One\n\n```csharp\nvar x = 1;\n");
        Assert.Contains(problems, p => p.Contains("code fence", StringComparison.Ordinal));
    }

    [Fact]
    public void A_table_without_a_separator_is_reported()
    {
        var problems = BlogMarkdown.Problems("## One\n\n| Option | Cost |\n| A | low |\n");
        Assert.Contains(problems, p => p.Contains("no separator row", StringComparison.Ordinal));
    }

    [Fact]
    public void A_ragged_table_row_is_reported()
    {
        var problems = BlogMarkdown.Problems("## One\n\n| Option | Cost |\n| --- | --- |\n| A | low | extra |\n");
        Assert.Contains(problems, p => p.Contains("columns", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_link_and_a_missing_structure_are_reported()
    {
        var problems = BlogMarkdown.Problems("Just prose with a [dead link]() and no headings.");
        Assert.Contains(problems, p => p.Contains("no destination", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("no H2 sections", StringComparison.Ordinal));
    }

    [Fact]
    public void Pipes_inside_a_code_fence_are_not_read_as_a_table()
    {
        const string markdown = """
            ## One

            ```text
            | not | a | table |
            ```
            """;
        Assert.Empty(BlogMarkdown.Problems(markdown));
    }
}

/// <summary>The three stages, through the real pipeline with a scripted model.</summary>
[Collection("api")]
public sealed class BlogThreeStageTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task The_edit_stage_replaces_the_draft_and_its_notes_become_warnings()
    {
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Scoped<IFoundryClientFactory>(
                _ => new AiGenerationTests.FakeFoundryFactory()))));

        var (client, campaignId, transcriptId) = await SetUpAsync(app);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/generate/blog", new { transcriptArtifactId = transcriptId });
        var result = await response.Content.ReadFromJsonAsync<Castmill.Core.Ai.GenerationResult>();
        Assert.True(result!.Success, result.Error);

        // The published body is the EDITED article, not the draft.
        var artifact = await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaignId}/artifacts/{result.ArtifactId}");
        Assert.Contains("Edited section", artifact!.ContentJson, StringComparison.Ordinal);

        // Editorial notes surface as warnings and never reach the article.
        Assert.Contains(result.ValidationWarnings, w => w.StartsWith("Editor:", StringComparison.Ordinal));
        Assert.DoesNotContain("Editorial notes", artifact.ContentJson, StringComparison.Ordinal);
    }

    private static async Task<(HttpClient Client, Guid CampaignId, Guid TranscriptId)> SetUpAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new Castmill.Core.Auth.RegisterRequest(
                $"blog-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Writer"));
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<Castmill.Core.Auth.AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);

        var campaign = await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Blog pipeline", null))).Content.ReadFromJsonAsync<CampaignResponse>();
        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaign!.Id}/transcripts",
            new { text = "We launched the product. It cut deployment time in half. The dashboard proves it.", source = "test" });
        var ingested = await ingest.Content.ReadFromJsonAsync<IngestedTranscript>();
        return (client, campaign.Id, ingested!.TranscriptArtifactId);
    }

    private sealed record IngestedTranscript(Guid TranscriptArtifactId, int SegmentCount);
}
