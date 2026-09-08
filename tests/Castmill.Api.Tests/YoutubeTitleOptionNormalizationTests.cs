using System.Text.Json;
using Castmill.Api.Services.Ai;
using Castmill.Core.Ai;

namespace Castmill.Api.Tests;

public sealed class YoutubeTitleOptionNormalizationTests
{
    [Fact]
    public void Synonym_and_duplicate_angles_are_repaired_before_validation()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "title": "Deployment Automation Cuts Delivery Time",
              "titleOptions": [
                { "slot": "A", "title": "Deployment Automation Cuts Delivery Time", "angle": "search engine optimization", "score": 91, "rationale": "Measured result" },
                { "slot": "B", "title": "The Workflow Behind Faster Shipping", "angle": "curiosity", "score": 84, "rationale": "Knowledge gap" },
                { "slot": "C", "title": "Slow Deployments? Fix the Workflow", "angle": "curiosity", "score": 82, "rationale": "Names the pain" }
              ],
              "description": "Deployment automation cut delivery time in half with a grounded workflow.",
              "chapters": [
                { "startSeconds": 0, "title": "Deployment automation" },
                { "startSeconds": 8, "title": "Delivery dashboard" },
                { "startSeconds": 16, "title": "Shipping workflow" }
              ],
              "suggestedPinnedComment": "Where would this workflow remove the most delay for your team?",
              "citations": ["s01"]
            }
            """);
        var transcript = new TranscriptContent(
            "test", [new TranscriptSegment("s01", 0, 30, null, "Deployment proof")]);

        var normalized = Generators.NormalizeYoutubeTitleOptions(document.RootElement);
        var options = normalized.GetProperty("titleOptions");

        Assert.Equal(["A", "B", "C"], options.EnumerateArray()
            .Select(option => option.GetProperty("slot").GetString()));
        Assert.Equal(["seo", "curiosity", "problem-solution"], options.EnumerateArray()
            .Select(option => option.GetProperty("angle").GetString()));
        Assert.True(Generators.ValidateYoutube(normalized, transcript).Passed);
    }

    [Theory]
    [InlineData("\"not-an-object\"")]
    [InlineData("""
        {
          "description": "A useful description",
          "titleOptions": [
            { "slot": 1, "title": "One", "angle": "seo", "score": 90 },
            { "slot": "B", "title": "Two", "angle": "curiosity", "score": 80 },
            { "slot": "C", "title": "Three", "angle": "problem-solution", "score": 70 }
          ],
          "chapters": [
            { "startSeconds": 0, "title": "First" },
            { "startSeconds": 10, "title": "Second" },
            { "startSeconds": 20, "title": "Third" }
          ],
          "suggestedPinnedComment": "What would you try first?",
          "citations": ["s01"]
        }
        """)]
    [InlineData("""
        {
          "description": "A useful description",
          "titleOptions": [
            { "slot": "A", "title": "One", "angle": "seo", "score": 90 },
            { "slot": "B", "title": "Two", "angle": "curiosity", "score": 80 },
            { "slot": "C", "title": "Three", "angle": "problem-solution", "score": 70 }
          ],
          "chapters": [
            { "startSeconds": "0:00", "title": "First" },
            "not-a-chapter",
            { "startSeconds": 20, "title": "Third" }
          ],
          "suggestedPinnedComment": "What would you try first?",
          "citations": ["s01"]
        }
        """)]
    public void Malformed_model_shapes_fail_validation_without_throwing(string modelJson)
    {
        using var document = JsonDocument.Parse(modelJson);
        var transcript = new TranscriptContent(
            "test", [new TranscriptSegment("s01", 0, 30, null, "Deployment proof")]);

        var outcome = Generators.ValidateYoutube(document.RootElement, transcript);

        Assert.False(outcome.Passed);
        Assert.False(string.IsNullOrWhiteSpace(outcome.FatalError));
    }

    [Fact]
    public void Display_timestamps_are_canonicalized_to_numeric_chapter_starts()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "description": "A useful description grounded in the deployment evidence.",
              "titleOptions": [
                { "slot": "A", "title": "One", "angle": "seo", "score": 90 },
                { "slot": "B", "title": "Two", "angle": "decision", "score": 80 },
                { "slot": "C", "title": "Three", "angle": "technical", "score": 70 }
              ],
              "chapters": [
                { "timestamp": "00:00", "title": "Deployment overview" },
                { "time": "0:10", "title": "Build versus buy" },
                { "startTime": "00:20", "title": "Performance setup" },
                "01:20 Performance results"
              ],
              "suggestedPinnedComment": "What would you try first?",
              "citations": ["s01"]
            }
            """);
        var transcript = new TranscriptContent(
            "test", [new TranscriptSegment("s01", 0, 90, null, "Deployment proof")]);

        var normalized = Generators.NormalizeYoutubeTitleOptions(document.RootElement);
        var starts = normalized.GetProperty("chapters").EnumerateArray()
            .Select(chapter => chapter.GetProperty("startSeconds").GetDouble());

        Assert.Equal([0d, 10d, 20d, 80d], starts);
        Assert.True(Generators.ValidateYoutube(normalized, transcript).Passed);
    }
}
