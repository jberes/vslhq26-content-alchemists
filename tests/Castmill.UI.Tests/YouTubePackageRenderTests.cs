using Castmill.UI.Editor;

namespace Castmill.UI.Tests;

/// <summary>
/// The YouTube package rendered as the raw JSON envelope on screen — braces, escaped \n, the
/// whole payload — because "youtube" was missing from the structured-kind list. Focus then
/// treated it as a markdown-bodied artifact, found no "markdown" field, and fell back to
/// dumping the source.
///
/// A kind that is a PACKAGE (title + alternates + description + chapters + tags) and not a
/// prose body has to be declared as one, and has to have a renderer. Both are pinned here.
/// </summary>
public sealed class YouTubePackageRenderTests
{
    private const string Payload = """
        {"content":{
          "title":"React Data Grid: Build Rich Data Experiences",
          "titleVariants":[
            "React Data Grid Features for Enterprise Apps",
            "How to Set Up a React Data Grid in Minutes",
            "React Data Grid for Large, Interactive Data Sets"],
          "description":"Build a React Data Grid with column pinning.\nSee how it works.",
          "chapters":[{"startSeconds":0,"title":"Introduction"},
                      {"startSeconds":45,"title":"Pivot grids"}],
          "tags":["React Data Grid","Ignite UI React Grid"],
          "citations":["S02"]},
         "validation":{"passed":true,"warnings":[]}}
        """;

    [Fact]
    public void YouTube_is_a_structured_kind()
    {
        // The declaration IS the bug fix — without it nothing else here is reached.
        Assert.True(StructuredContent.IsStructured("youtube"));
    }

    [Fact]
    public void The_package_renders_as_readable_sections_not_raw_json()
    {
        var markdown = StructuredContent.ToDisplayMarkdown("youtube", Payload);

        Assert.Contains("# React Data Grid: Build Rich Data Experiences", markdown, StringComparison.Ordinal);
        Assert.Contains("## Title options to A/B test", markdown, StringComparison.Ordinal);
        Assert.Contains("## Description", markdown, StringComparison.Ordinal);
        Assert.Contains("## Chapters", markdown, StringComparison.Ordinal);
        Assert.Contains("## Tags", markdown, StringComparison.Ordinal);

        // The failure mode, named: no JSON scaffolding may survive into the rendered view.
        Assert.DoesNotContain("\"titleVariants\"", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\"startSeconds\"", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\"validation\"", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\\n", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Exactly_three_title_alternates_are_listed_each_with_its_length()
    {
        var markdown = StructuredContent.ToDisplayMarkdown("youtube", Payload);

        Assert.Contains("1. React Data Grid Features for Enterprise Apps", markdown, StringComparison.Ordinal);
        Assert.Contains("2. How to Set Up a React Data Grid in Minutes", markdown, StringComparison.Ordinal);
        Assert.Contains("3. React Data Grid for Large, Interactive Data Sets", markdown, StringComparison.Ordinal);

        // The character count is the actionable part: past ~60 YouTube truncates the title in
        // search, and a writer should not have to count.
        Assert.Contains("(44 chars)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Chapters_render_as_timestamps_a_person_can_paste()
    {
        var markdown = StructuredContent.ToDisplayMarkdown("youtube", Payload);

        Assert.Contains("**0:00** Introduction", markdown, StringComparison.Ordinal);
        Assert.Contains("**0:45** Pivot grids", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_payload_missing_optional_sections_still_renders()
    {
        // Generators do fail partially; a package with only a title must not throw or fall
        // back to raw JSON.
        var markdown = StructuredContent.ToDisplayMarkdown(
            "youtube", """{"content":{"title":"Just a title"}}""");

        Assert.Contains("# Just a title", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("{", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Scored_slots_and_the_grounded_pinned_comment_render_as_first_class_fields()
    {
        const string scored = """
            {"content":{"title":"Deployment Automation Results","titleOptions":[
              {"slot":"A","title":"Deployment Automation Results","angle":"seo","score":92,"rationale":"Concrete payoff"},
              {"slot":"B","title":"What Changed Our Deployments?","angle":"curiosity","score":86,"rationale":"Knowledge gap"},
              {"slot":"C","title":"Slow Deployments? Fix the Workflow","angle":"problem-solution","score":83,"rationale":"Names the pain"}],
              "description":"A complete description.",
              "suggestedPinnedComment":"The source measured a 50% improvement—where is your biggest delay?",
              "chapters":[{"startSeconds":0,"title":"Deployment automation"}],"tags":["automation"]}}
            """;

        var markdown = StructuredContent.ToDisplayMarkdown("youtube", scored);
        Assert.Contains("## Scored title experiment", markdown, StringComparison.Ordinal);
        Assert.Contains("### A · seo · 92/100", markdown, StringComparison.Ordinal);
        Assert.Contains("### C · problem-solution · 83/100", markdown, StringComparison.Ordinal);
        Assert.Contains("## Suggested pinned comment", markdown, StringComparison.Ordinal);
        Assert.Contains("where is your biggest delay?", markdown, StringComparison.OrdinalIgnoreCase);
    }
}
