using System.Text.Json;
using Castmill.Core.Ai;
using Microsoft.Extensions.AI;

namespace Castmill.Api.Services.Ai;

public interface IResearchContextSuggester
{
    Task<ResearchContextSuggestionResponse> SuggestAsync(
        Guid userId, TranscriptContent transcript, CancellationToken ct);
}

/// <summary>
/// Infers only the audience needed to shape research. Keeping this separate from
/// <see cref="IBriefSuggester"/> prevents titles, angles and other production content from
/// being generated before the SEO/AEO report has been approved.
/// </summary>
public sealed class ResearchContextSuggester(IChatProviderRegistry chatProviders)
    : IResearchContextSuggester
{
    public async Task<ResearchContextSuggestionResponse> SuggestAsync(
        Guid userId, TranscriptContent transcript, CancellationToken ct)
    {
        var prompt = $$"""
            Infer the specific audience an SEO/AEO analyst should research for this transcript.

            Reply with JSON only, no prose:
            {
              "audience": string
            }

            Rules:
            - Describe the people most likely to search for and benefit from this material.
            - Be precise about role, situation, intent and relevant sophistication.
            - Avoid broad labels such as "developers", "marketers" or "business leaders".
            - Infer only from the transcript. Do not create a title, content angle, keyword,
              campaign copy or unsupported market claim.
            - Write one concise phrase suitable for an editable form field.

            Transcript:
            {{TranscriptService.ToPromptText(transcript)}}
            """;

        var client = await chatProviders.ResolveAsync(userId, "chat", ct);
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
        var root = CitationMarkers.Strip(AiOrchestrator.ParseModelJson(response.Text));

        var audience = root.TryGetProperty("audience", out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;

        return new ResearchContextSuggestionResponse(audience);
    }
}
