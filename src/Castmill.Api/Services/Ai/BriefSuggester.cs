using System.Text.Json;
using Castmill.Core.Ai;

namespace Castmill.Api.Services.Ai;

/// <summary>
/// Everything step 3 of the run flow asks a human to type, read off the transcript instead.
/// The summary is the part that earns its keep on its own: it is the only place the user sees
/// what the machine actually understood before committing to a full fan-out.
/// </summary>
public sealed record BriefSuggestion(
    string? Title,
    string? Audience,
    string? BrandVoice,
    string? Angle,
    string? Summary,
    IReadOnlyList<string> KeyPoints);

public interface IBriefSuggester
{
    Task<BriefSuggestion> SuggestAsync(
        Guid userId, TranscriptContent transcript, string? currentTitle, CancellationToken ct);
}

public sealed class BriefSuggester(IChatProviderRegistry chatProviders) : IBriefSuggester
{
    public async Task<BriefSuggestion> SuggestAsync(
        Guid userId, TranscriptContent transcript, string? currentTitle, CancellationToken ct)
    {
        var prompt = $$"""
            Read this transcript and fill out a campaign brief for a content team about to
            generate a blog, social posts, a newsletter and video clips from it.

            Reply with JSON only, no prose:
            {
              "title": string,        // a specific, publishable campaign title — not "Webinar recording"
              "audience": string,     // who this is for, inferred from what is assumed and explained
              "brandVoice": string,   // how the speaker actually talks, as an instruction to a writer
              "angle": string,        // the one thing that makes this worth publishing
              "summary": string,      // 3-5 sentences on what this covers and what it argues
              "keyPoints": [ string ] // 3-6 specific claims or moments a writer should not miss
            }

            Rules:
            - Be specific. "Developers" is a useless audience; "platform engineers evaluating
              build tooling who already use Docker" is a useful one.
            - "brandVoice" describes THIS speaker's register — pace, formality, humour, how they
              handle jargon — not a generic house style.
            - "angle" is a claim, not a topic.
            - Ground every field in the transcript. Do not invent products, numbers or names.
            {{(string.IsNullOrWhiteSpace(currentTitle)
                ? string.Empty
                : $"- The user already named this \"{currentTitle}\". Improve it only if the transcript clearly warrants it; otherwise keep it.")}}

            Transcript:
            {{TranscriptService.ToPromptText(transcript)}}
            """;

        var client = await chatProviders.ResolveAsync(userId, "chat", ct);
        var response = await client.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, prompt)],
            cancellationToken: ct);

        // Markers would read as noise in a form field, and this output goes straight into one.
        var root = CitationMarkers.Strip(AiOrchestrator.ParseModelJson(response.Text));

        return new BriefSuggestion(
            Str(root, "title") ?? currentTitle,
            Str(root, "audience"),
            Str(root, "brandVoice"),
            Str(root, "angle"),
            Str(root, "summary"),
            Strings(root, "keyPoints"));

        static string? Str(JsonElement root, string name) =>
            root.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()!.Trim()
                : null;

        static IReadOnlyList<string> Strings(JsonElement root, string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? [.. v.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!.Trim())
                    .Where(s => s.Length > 0)
                    .Take(8)]
                : [];
    }
}
