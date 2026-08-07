using System.Diagnostics;
using System.Text.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Knowledge;
using Castmill.Api.Services.Seo;
using Castmill.Core;
using Castmill.Core.Ai;
using Microsoft.Extensions.AI;

namespace Castmill.Api.Services.Scout;

public interface IContentScout
{
    Task<ScoutResult> RunAsync(
        Guid userId, Campaign campaign, string? focus, int wanted, CancellationToken ct);
}

/// <summary>
/// Proposes what to write next, and — as often — what NOT to write.
///
/// This is the one place in the product where an agent loop genuinely earns its keep: the
/// number of steps is not knowable up front. How many keyword clusters are worth checking,
/// how many follow-up queries a marginal one needs, and whether an opportunity is new, a
/// refresh, or already covered all depend on what the previous tool call returned.
///
/// Built on <see cref="Microsoft.Extensions.AI"/>'s function invocation rather than a
/// provider-specific agent SDK, so the Scout runs identically on Foundry or on the ADR-020
/// Anthropic client and ADR-005 stays intact.
///
/// Every tool call is recorded in the prompt log. This codebase's whole posture is narrated,
/// inspectable work — determinate progress, no indeterminate spinners, a transparency log —
/// and an agent that thinks silently for forty seconds would be the first black box in the
/// product. It does not need to be one: the tool calls ARE the progress narration.
/// </summary>
public sealed class ContentScout(
    IChatProviderRegistry chatProviders,
    IContentInventory inventory,
    IKnowledgeBaseClient knowledge,
    ISeoProvider seo,
    IPromptLog promptLog,
    TimeProvider clock,
    ILogger<ContentScout> logger) : IContentScout
{
    /// <summary>
    /// Hard ceiling on tool calls. An agent that cannot finish inside this has misunderstood
    /// the task, and letting it keep going would spend the user's budget discovering that.
    /// </summary>
    private const int MaxToolCalls = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ScoutResult> RunAsync(
        Guid userId, Campaign campaign, string? focus, int wanted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var stopwatch = Stopwatch.StartNew();
        var trace = new List<ScoutStep>();
        var calls = 0;

        // The tools. Each one is deliberately narrow: the model chooses WHICH question to
        // ask, never how the answer is computed.
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                async (string query) =>
                {
                    if (++calls > MaxToolCalls) { return "Tool budget spent — propose with what you have."; }
                    var answer = await knowledge.AskAsync(userId, query, ct);
                    trace.Add(new ScoutStep("search_published", query,
                        answer is null ? "nothing published found" : $"{answer.Citations.Count} source(s)"));
                    return answer is null
                        ? "No published coverage found (or no knowledge base is configured)."
                        : answer.ToPromptBlock();
                },
                "search_published",
                "Ask the customer's knowledge base what has already been PUBLISHED on a topic. "
                + "Returns a summary plus real source URLs — those URLs are the coverage evidence."),

            AIFunctionFactory.Create(
                async (string query) =>
                {
                    if (++calls > MaxToolCalls) { return "Tool budget spent — propose with what you have."; }
                    var hits = await inventory.SearchAsync(query, 8, ct);
                    trace.Add(new ScoutStep("search_our_drafts", query, $"{hits.Count} match(es)"));
                    return hits.Count == 0
                        ? "Nothing drafted on this."
                        : JsonSerializer.Serialize(hits, Json);
                },
                "search_our_drafts",
                "Search content we have already DRAFTED but may not have published. A piece in "
                + "review is not on the site yet, so the knowledge base cannot see it, but "
                + "proposing it again would still be waste."),

            AIFunctionFactory.Create(
                async (string seed) =>
                {
                    if (++calls > MaxToolCalls) { return "Tool budget spent — propose with what you have."; }
                    if (!seo.IsConfigured)
                    {
                        return "No SEO provider configured — judge demand from the transcript and the brief instead.";
                    }
                    var suggestions = await seo.GetSuggestionsAsync(seed, 15, ct);
                    trace.Add(new ScoutStep("keyword_ideas", seed, $"{suggestions.Count} keyword(s)"));
                    return JsonSerializer.Serialize(suggestions, Json);
                },
                "keyword_ideas",
                "Related search terms for a seed phrase, with monthly volume and difficulty. "
                + "Use it to judge whether anyone is actually searching for a topic."),
        };

        var client = await chatProviders.ResolveAsync(userId, "chat", ct);
        // The loop itself: the SDK executes tool calls and feeds results back until the model
        // stops asking. Provider-agnostic, so this works on either family (ADR-005/020).
        using var agent = new ChatClientBuilder(client)
            .UseFunctionInvocation()
            .Build();

        var prompt = BuildPrompt(campaign, focus, wanted);
        var responseText = string.Empty;
        var success = false;

        try
        {
            var response = await agent.GetResponseAsync(prompt, new ChatOptions { Tools = tools }, ct);
            responseText = response.Text;
            success = true;

            return new ScoutResult(true, null, Parse(responseText), trace, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Content scout failed for campaign {CampaignId}", campaign.Id);
            return new ScoutResult(
                false,
                ex is AiNotConfiguredException ? ex.Message : $"Scout failed: {ex.GetType().Name}",
                [], trace, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            promptLog.Record(new PromptLogEntry(
                clock.GetUtcNow(), userId, "content-scout", "chat",
                Excerpt(prompt), Excerpt(responseText), success, stopwatch.ElapsedMilliseconds));
        }
    }

    private static string BuildPrompt(Campaign campaign, string? focus, int wanted) =>
        $$"""
        You are a content strategist for the campaign "{{campaign.Name}}".
        {{(string.IsNullOrWhiteSpace(focus) ? "" : $"The team is focused on: {focus}\n")}}
        Propose up to {{wanted}} pieces of content worth making next.

        Work like this, using the tools:
        1. Decide the handful of topics this campaign should own.
        2. For EACH one, check what has already been published (search_published) AND what has
           already been drafted (search_our_drafts) before proposing it. Do not skip this —
           telling the team something is already covered is more valuable than telling them to
           write it again.
        3. Use keyword_ideas where search demand is the deciding factor.

        Then answer with ONLY this JSON — no fences, no commentary:
        {
          "suggestions": [
            {
              "kind": "blog" | "social-linkedin" | "newsletter" | "email-sequence" | "landing-page" | "show-notes",
              "title": string,
              "angle": string,
              "targetKeywords": string[],
              "rationale": string,
              "coverage": "new" | "refresh" | "covered",
              "evidence": [ { "title": string, "url": string } ]
            }
          ]
        }

        Rules for "coverage":
        - "covered" — we already have this. Say so, and cite the URL. Still include it: a
          suggestion NOT to write something is a real answer.
        - "refresh" — something exists but is out of date or thin. Cite it.
        - "new" — genuinely uncovered.
        Every "covered" and "refresh" MUST carry evidence. An unevidenced claim that something
        exists is worse than no claim.
        """;

    /// <summary>Internal so tests exercise the real parser rather than a copy of it.</summary>
    internal static IReadOnlyList<ScoutSuggestion> Parse(string text)
    {
        try
        {
            var json = AiOrchestrator.ParseModelJson(text);
            if (!json.TryGetProperty("suggestions", out var suggestions)
                || suggestions.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. suggestions.EnumerateArray()
                .Select(s => new ScoutSuggestion(
                    Text(s, "kind") ?? "blog",
                    Text(s, "title") ?? string.Empty,
                    Text(s, "angle") ?? string.Empty,
                    Strings(s, "targetKeywords"),
                    Text(s, "rationale") ?? string.Empty,
                    Text(s, "coverage") ?? "new",
                    Evidence(s)))
                .Where(s => s.Title.Length > 0)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<ScoutEvidence> Evidence(JsonElement suggestion)
    {
        if (!suggestion.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. evidence.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .Select(e => new ScoutEvidence(Text(e, "title") ?? string.Empty, Text(e, "url") ?? string.Empty))
            .Where(e => e.Url.Length > 0)];
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : null)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)]
            : [];

    private static string Excerpt(string value) =>
        value.Length <= PromptLog.ExcerptLength ? value : value[..PromptLog.ExcerptLength];
}
