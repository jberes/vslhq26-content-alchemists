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
}