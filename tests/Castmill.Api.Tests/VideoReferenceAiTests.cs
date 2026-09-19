using System.Text.Json;
using Castmill.Api.Services.Ai;
using Castmill.Core.Resources;

namespace Castmill.Api.Tests;

public sealed class VideoReferenceAiTests
{
    private static VideoReferenceAiRequest Request() => new(
        "ui-demonstration", 2, 3840, 2160,
        [new("a", 10, "AA==", 80), new("b", 20, "AA==", 70)]);

    [Fact]
    public void Parser_keeps_only_supplied_candidate_ids_and_valid_source_crop()
    {
        using var json = JsonDocument.Parse("""
            {
              "rankings": [
                { "id": "b", "rank": 1, "reason": "Focused grid state" },
                { "id": "invented", "rank": 2, "reason": "must be ignored" },
                { "id": "a", "rank": 3, "reason": "Overview" }
              ],
              "crop": { "x": 0, "y": 180, "width": 3840, "height": 1980, "confidence": "high" }
            }
            """);

        var result = VideoReferenceAiService.Parse(json.RootElement, Request());

        Assert.True(result.Ran);
        Assert.Equal(["b", "a"], result.Rankings.Select(item => item.Id));
        Assert.Equal("Focused grid state", result.Rankings[0].Reason);
        Assert.NotNull(result.SuggestedCrop);
        Assert.Equal(180, result.SuggestedCrop.Y);
        Assert.Equal("automatic", result.SuggestedCrop.Method);
    }

    [Theory]
    [InlineData("low", 0, 0, 3840, 2160)]
    [InlineData("high", 3800, 0, 100, 2160)]
    public void Parser_refuses_low_confidence_or_out_of_bounds_crops(
        string confidence, int x, int y, int width, int height)
    {
        using var json = JsonDocument.Parse($$"""
            { "rankings": [], "crop": { "x": {{x}}, "y": {{y}}, "width": {{width}}, "height": {{height}}, "confidence": "{{confidence}}" } }
            """);

        var result = VideoReferenceAiService.Parse(json.RootElement, Request());

        Assert.Null(result.SuggestedCrop);
    }

    [Fact]
    public void Prompt_makes_the_no_generation_and_ui_demo_contract_explicit()
    {
        var prompt = VideoReferenceAiService.BuildPrompt(Request());

        Assert.Contains("Do not", prompt, StringComparison.Ordinal);
        Assert.Contains("modify or regenerate", prompt, StringComparison.Ordinal);
        Assert.Contains("UI Demonstration", prompt, StringComparison.Ordinal);
        Assert.Contains("3840×2160", prompt, StringComparison.Ordinal);
    }
}
