using System.Text.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Knowledge;
using Castmill.Core.Ai;
using Castmill.Core.Resources;

namespace Castmill.Api.Services.Seo;

public interface ISeoResearch
{
    Task<SeoResearchResponse> ResearchAsync(
        Guid userId, TranscriptContent transcript, string? campaignName, CancellationToken ct);
}

/// <summary>
/// Keyword and question research run BEFORE generation, so the fan-out can be written against
/// real targets instead of being analysed after the fact.
///
/// Three sources, deliberately kept distinguishable in the output rather than blended:
///   • the model reads the transcript and proposes seed terms and questions it actually covers;
///   • DataForSEO expands those seeds and attaches volume/difficulty (keyword_suggestions +
///     search_volume) and returns the real People-Also-Ask box for the strongest seed;
///   • the customer knowledge base contributes the follow-up questions its own readers ask.
///
/// Every stage is optional. With no SEO credential this still returns the model's keywords,
/// flagged <c>HasProviderMetrics = false</c>, because a target with no volume number is still
/// a usable target and inventing a number would be worse than admitting there isn't one.
/// </summary>
public sealed class SeoResearch(
    IChatProviderRegistry chatProviders,
    ISeoProvider seo,
    IKnowledgeBaseClient knowledge,
    ILogger<SeoResearch> logger) : ISeoResearch
{
    /// <summary>Enough to rank meaningfully without turning the picker into a spreadsheet.</summary>
    private const int MaxKeywords = 24;
    private const int MaxQuestions = 12;

    public async Task<SeoResearchResponse> ResearchAsync(
        Guid userId, TranscriptContent transcript, string? campaignName, CancellationToken ct)
    {
        var notes = new List<string>();
        var seed = await SeedAsync(userId, transcript, campaignName, ct);

        var keywords = new List<SeoTarget>();
        var questions = new List<SeoQuestion>();

        foreach (var question in seed.Questions.Take(MaxQuestions))
        {
            questions.Add(new SeoQuestion(question, "transcript"));
        }

        var hasMetrics = false;
        if (seo.IsConfigured && seed.Keywords.Count > 0)
        {
            try
            {
                keywords.AddRange(await ExpandAsync(seed.Keywords, ct));
                hasMetrics = keywords.Count > 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                // Research must never be the thing that stops a run: degrade to the model's
                // own keywords and say so.
                logger.LogWarning(ex, "SEO provider failed during research; falling back to model keywords.");
                notes.Add("Search-volume data was unavailable, so these are the model's suggestions only.");
            }

            if (hasMetrics)
            {
                try
                {
                    // Google shows a People-Also-Ask box for QUESTIONS far more often than for
                    // noun phrases: "react data grid" returns none, "what is a data grid"
                    // returns four. Verified against the live API — so if the plain keyword
                    // comes back empty, ask the question form of it before giving up. The
                    // second call only happens when the first found nothing.
                    var top = keywords[0].Term;
                    var paa = await seo.GetQuestionsAsync(top, ct);

                    if (paa.Count == 0)
                    {
                        paa = await seo.GetQuestionsAsync($"what is {top}", ct);
                    }

                    foreach (var question in paa)
                    {
                        Add(questions, question, "paa");
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "People-also-ask lookup failed; keeping the other question sources.");
                    notes.Add("Google's \"people also ask\" was unavailable for the top keyword.");
                }
            }
        }
        else if (!seo.IsConfigured)
        {
            notes.Add("No SEO provider is configured, so there are no volume or difficulty numbers — "
                + "these keywords are the model's reading of the source.");
        }

        // Anything the provider did not return still belongs on the list: the model's seeds are
        // the ones grounded in what the source ACTUALLY says.
        foreach (var term in seed.Keywords)
        {
            if (!keywords.Any(k => string.Equals(k.Term, term, StringComparison.OrdinalIgnoreCase)))
            {
                keywords.Add(new SeoTarget(term, null, null, null, "model"));
            }
        }

        if (knowledge.IsConfigured && seed.Keywords.Count > 0)
        {
            try
            {
                var answer = await knowledge.AskAsync(
                    userId, $"What do people ask about {seed.Keywords[0]}?", ct);

                foreach (var suggestion in answer?.Suggestions ?? [])
                {
                    Add(questions, suggestion, "knowledge-base");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                logger.LogWarning(ex, "Knowledge base unavailable during research.");
            }
        }

        return new SeoResearchResponse(
            [.. keywords.OrderByDescending(k => k.Opportunity ?? -1).Take(MaxKeywords)],
            // Ordered by how much the question is WORTH, not by the order the sources happened
            // to answer in. A question Google actually shows beats one the model imagined, and
            // the UI pre-selects from the top — so appending PAA last meant the best questions
            // were the ones nobody picked.
            [.. questions.OrderBy(q => QuestionRank(q.Source)).Take(MaxQuestions)],
            hasMetrics,
            notes);

        static int QuestionRank(string source) => source switch
        {
            "paa" => 0,             // Google shows this box; people demonstrably ask it.
            "knowledge-base" => 1,  // our own readers asked it.
            _ => 2,                 // the model inferred it from the source.
        };

        static void Add(List<SeoQuestion> into, string? text, string source)
        {
            var trimmed = text?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed)
                && !into.Any(q => string.Equals(q.Question, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                into.Add(new SeoQuestion(trimmed, source));
            }
        }
    }

    /// <summary>
    /// Expands the model's seeds through DataForSEO and merges the two metric sources: labs
    /// suggestions carry difficulty, google_ads carries the authoritative volume for the exact
    /// seed terms. Both matter — ranking on volume alone picks unwinnable head terms.
    /// </summary>
    private async Task<List<SeoTarget>> ExpandAsync(IReadOnlyList<string> seeds, CancellationToken ct)
    {
        var merged = new Dictionary<string, SeoKeyword>(StringComparer.OrdinalIgnoreCase);

        // One expansion, from the strongest seed: each call is billed, and suggestions for
        // near-identical seeds overlap almost entirely.
        foreach (var suggestion in await seo.GetSuggestionsAsync(seeds[0], 40, ct))
        {
            merged[suggestion.Term] = suggestion;
        }

        foreach (var exact in await seo.GetKeywordMetricsAsync([.. seeds.Take(20)], ct))
        {
            // google_ads has no difficulty; keep the labs value when we already have one.
            merged[exact.Term] = merged.TryGetValue(exact.Term, out var existing)
                ? existing with { Volume = exact.Volume, Competition = exact.Competition, Cpc = exact.Cpc }
                : exact;
        }

        return [.. merged.Values
            .Where(k => k.Volume > 0 || seeds.Contains(k.Term, StringComparer.OrdinalIgnoreCase))
            .Select(k => new SeoTarget(
                k.Term, k.Volume, k.Difficulty,
                Math.Round(DataForSeoProvider.Opportunity(k), 2), "provider"))
            .OrderByDescending(k => k.Opportunity)];
    }

    private sealed record Seed(IReadOnlyList<string> Keywords, IReadOnlyList<string> Questions);

    /// <summary>
    /// The model reads the transcript for terms and questions the content genuinely covers.
    /// This grounding matters: expanding from a keyword the source never addresses produces a
    /// beautifully ranked list of things this content cannot rank for.
    /// </summary>
    private async Task<Seed> SeedAsync(
        Guid userId, TranscriptContent transcript, string? campaignName, CancellationToken ct)
    {
        var prompt = $$"""
            Read this transcript and propose search targets for the content that will be written
            from it.

            Reply with JSON only:
            { "keywords": [ string ], "questions": [ string ] }

            "keywords": 8-12 phrases someone would actually type into Google to find this
            content. Mix 2-3 short head terms with specific long-tail phrases. Every one must be
            something the transcript genuinely covers — a term the source does not address is a
            target this content cannot win.

            "questions": 6-10 questions this content actually answers, phrased the way a person
            would ask them out loud. These drive answer-engine optimisation, so prefer questions
            with a definite answer in the source over open-ended ones.

            Campaign: {{campaignName ?? "(unnamed)"}}

            Transcript:
            {{TranscriptService.ToPromptText(transcript)}}
            """;

        try
        {
            var client = await chatProviders.ResolveAsync(userId, "chat", ct);
            var response = await client.GetResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, prompt)],
                cancellationToken: ct);

            var root = AiOrchestrator.ParseModelJson(response.Text);
            return new Seed(Strings(root, "keywords"), Strings(root, "questions"));
        }
        catch (Exception ex) when (ex is JsonException or AiNotConfiguredException)
        {
            logger.LogWarning(ex, "Seed keyword generation failed; research returns nothing to pick from.");
            return new Seed([], []);
        }

        static IReadOnlyList<string> Strings(JsonElement root, string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? [.. v.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!.Trim())
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)]
                : [];
    }
}
