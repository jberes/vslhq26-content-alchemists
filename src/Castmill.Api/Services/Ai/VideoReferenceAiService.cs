using System.Text.Json;
using Castmill.Core.Resources;
using Microsoft.Extensions.AI;

namespace Castmill.Api.Services.Ai;

public interface IVideoReferenceAiService
{
    Task<VideoReferenceAiResponse> AnalyzeAsync(
        Guid userId, VideoReferenceAiRequest request, CancellationToken ct);
}

/// <summary>
/// Optional semantic pass over low-resolution candidates. It can rank identifiers and
/// propose an integer crop, but it never receives the video and never returns image bytes.
/// Any provider/model/JSON failure becomes Ran=false so deterministic extraction continues.
/// </summary>
public sealed class VideoReferenceAiService(
    IChatProviderRegistry chatProviders,
    ILogger<VideoReferenceAiService> logger) : IVideoReferenceAiService
{
    public async Task<VideoReferenceAiResponse> AnalyzeAsync(
        Guid userId, VideoReferenceAiRequest request, CancellationToken ct)
    {
        try
        {
            var content = new List<AIContent> { new TextContent(BuildPrompt(request)) };
            foreach (var candidate in request.Candidates)
            {
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(candidate.JpegBase64);
                }
                catch (FormatException)
                {
                    return new VideoReferenceAiResponse(false, [], null, "low",
                        $"Candidate {candidate.Id} is not valid base64.");
                }
                content.Add(new TextContent($"Candidate {candidate.Id} at {candidate.TimestampSeconds:0.###} seconds:"));
                content.Add(new DataContent(bytes, "image/jpeg"));
            }

            var client = await chatProviders.ResolveAsync(userId, "chat-audit", ct);
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, content)], cancellationToken: ct);
            return Parse(AiOrchestrator.ParseModelJson(response.Text), request);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Video reference AI ranking unavailable; deterministic selection remains active");
            return new VideoReferenceAiResponse(false, [], null, "low",
                "AI ranking was unavailable; deterministic selection was kept.");
        }
    }

    internal static string BuildPrompt(VideoReferenceAiRequest request) => $$"""
        Rank these exact video-frame candidates for the goal "{{request.Goal}}". Do not
        modify or regenerate any image. Prefer clear, distinct, useful moments. Avoid blank,
        blurry, transitional, duplicate, developer-tool, unrelated-tab, and loading states.
        For UI Demonstration prefer distinct interface states (overview, focus/selection,
        filtering, menus, completed actions, accessibility feedback). For Product prefer a
        faithful unobstructed product view. For People prefer clear expressions and poses.

        Also inspect stable recording chrome. Propose a crop only when it cleanly removes
        browser tabs/address bars, window borders, or solid recording margins without
        removing application content. Coordinates are integer source pixels in a
        {{request.SourceWidth}}×{{request.SourceHeight}} frame. Confidence is high, medium,
        or low. Low confidence must use the full frame.

        Return JSON only:
        {
          "rankings": [{ "id": "candidate id", "rank": 1, "reason": "short reason" }],
          "crop": { "x": 0, "y": 0, "width": {{request.SourceWidth}}, "height": {{request.SourceHeight}}, "confidence": "low" }
        }
        Return at most {{request.Quantity}} rankings and only identifiers supplied below.
        """;

    internal static VideoReferenceAiResponse Parse(JsonElement root, VideoReferenceAiRequest request)
    {
        var allowed = request.Candidates.Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
        var rankings = new List<VideoReferenceAiRank>();
        if (root.TryGetProperty("rankings", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                var id = row.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                if (id is null || !allowed.Contains(id)) continue;
                var rank = row.TryGetProperty("rank", out var rankValue) && rankValue.TryGetInt32(out var parsedRank)
                    ? parsedRank : rankings.Count + 1;
                var reason = row.TryGetProperty("reason", out var reasonValue)
                    ? reasonValue.GetString()?.Trim() : null;
                rankings.Add(new VideoReferenceAiRank(id, Math.Max(1, rank), reason ?? "AI-selected distinct frame"));
            }
        }
        rankings = rankings.OrderBy(item => item.Rank).DistinctBy(item => item.Id)
            .Take(request.Quantity).ToList();

        VideoReferenceCropDto? crop = null;
        var confidence = "low";
        if (root.TryGetProperty("crop", out var cropValue) && cropValue.ValueKind == JsonValueKind.Object)
        {
            confidence = cropValue.TryGetProperty("confidence", out var confidenceValue)
                ? confidenceValue.GetString()?.Trim().ToLowerInvariant() ?? "low" : "low";
            var x = ReadInt(cropValue, "x");
            var y = ReadInt(cropValue, "y");
            var width = ReadInt(cropValue, "width");
            var height = ReadInt(cropValue, "height");
            if (confidence is "high" or "medium"
                && x >= 0 && y >= 0 && width > 0 && height > 0
                && x + width <= request.SourceWidth && y + height <= request.SourceHeight)
            {
                crop = new VideoReferenceCropDto(x, y, width, height, "automatic",
                    confidence == "high" ? .9 : .6);
            }
        }
        return new VideoReferenceAiResponse(true, rankings, crop, confidence);
    }

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : -1;
}
