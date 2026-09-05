using System.Diagnostics;
using System.Text.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Ai.Agents;
using Castmill.Api.Services.Knowledge;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Services.Seo;

/// <summary>
/// The SEO research agent (ADR-058). The fixed pipeline expanded whatever seeds the model
/// first guessed; this lets the model drive the DataForSEO tools — expand a seed, price a
/// candidate, read the live SERP, pull People-Also-Ask, ask the knowledge base — and decide
/// when the plan is good enough, under a hard tool budget. Output is the same
/// <see cref="SeoResearchResponse"/> the rest of the product already consumes, and the fixed
/// pipeline remains the fallback for a disabled agent, an unconfigured provider or a failure.
/// </summary>
public sealed class SeoResearchAgent(
    SeoResearch inner,
    IChatProviderRegistry chatProviders,
    ISeoProvider seo,
    IKnowledgeBaseClient knowledge,
    IOptions<AiOptions> options,
    IPromptLog promptLog,
    TimeProvider clock,
    ILogger<SeoResearchAgent> logger) : ISeoResearch
{
    private const int MaxKeywords = 24;
    private const int MaxQuestions = 12;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SeoResearchResponse> ResearchAsync(
        Guid userId, TranscriptContent transcript, string? campaignName, CancellationToken ct)
    {
        var settings = options.Value.Agents.SeoResearch;
        if (!settings.Enabled || !seo.IsConfigured)
        {
            return await inner.ResearchAsync(userId, transcript, campaignName, ct);
        }

        var stopwatch = Stopwatch.StartNew();
        var calls = 0;
        var trace = new List<AgentStep>();
        var metrics = new Dictionary<string, SeoKeyword>(StringComparer.OrdinalIgnoreCase);
        var lookups = new HashSet<string>(StringComparer.Ordinal);
        var paaQuestions = new List<string>();
        var kbQuestions = new List<string>();

        string Spent() => "Tool budget spent — finish the plan with the data you already have.";

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                async (string seed) =>
                {
                    if (++calls > settings.MaxToolCalls) return Spent();
                    var rows = await seo.GetSuggestionsAsync(seed, 25, ct);
                    lookups.Add("dataforseo_labs/google/keyword_suggestions/live");
                    foreach (var row in rows) metrics[row.Term] = row;
                    trace.Add(new AgentStep("keyword_ideas", seed, $"{rows.Count} rows"));
                    return JsonSerializer.Serialize(rows.Select(Compact), Json);
                },
                "keyword_ideas",
                "Related search phrases for a seed, with monthly volume, difficulty, CPC and intent. "
                + "Use several seeds: a head term, the product name, and the problem the content solves."),

            AIFunctionFactory.Create(
                async (string[] keywords) =>
                {
                    if (++calls > settings.MaxToolCalls) return Spent();
                    var rows = await seo.GetKeywordMetricsAsync(keywords.Take(20).ToList(), ct);
                    lookups.Add("dataforseo_labs/google/keyword_overview/live");
                    foreach (var row in rows) metrics[row.Term] = row;
                    trace.Add(new AgentStep("keyword_metrics", string.Join(", ", keywords.Take(5)), $"{rows.Count} rows"));
                    return JsonSerializer.Serialize(rows.Select(Compact), Json);
                },
                "keyword_metrics",
                "Exact volume and difficulty for phrases you already have in mind (up to 20)."),

            AIFunctionFactory.Create(
                async (string keyword) =>
                {
                    if (++calls > settings.MaxToolCalls) return Spent();
                    var snapshot = await seo.GetSerpSnapshotAsync(keyword, ct);
                    lookups.Add("serp/google/organic/live/advanced");
                    trace.Add(new AgentStep("serp_snapshot", keyword, $"{snapshot.OrganicResults.Count} results"));
                    return JsonSerializer.Serialize(new
                    {
                        keyword,
                        aiOverview = snapshot.AiOverview is { Length: > 0 },
                        featuredSnippet = snapshot.FeaturedSnippet is { Length: > 0 },
                        top = snapshot.OrganicResults.Take(10).Select(r => new { r.Rank, r.Domain, r.Title }),
                    }, Json);
                },
                "serp_snapshot",
                "Who ranks today for a phrase (top 10 titles and domains) and whether Google shows an "
                + "AI Overview or a featured snippet — tells you the intent and how hard the page is to win."),

            AIFunctionFactory.Create(
                async (string keyword) =>
                {
                    if (++calls > settings.MaxToolCalls) return Spent();
                    var questions = await seo.GetQuestionsAsync(keyword, ct);
                    lookups.Add("serp/google/organic/live/advanced");
                    foreach (var q in questions) if (!paaQuestions.Contains(q, StringComparer.OrdinalIgnoreCase)) paaQuestions.Add(q);
                    trace.Add(new AgentStep("people_also_ask", keyword, $"{questions.Count} questions"));
                    return questions.Count == 0 ? "No People-Also-Ask box for that phrase." : JsonSerializer.Serialize(questions, Json);
                },
                "people_also_ask",
                "Google's People-Also-Ask questions for a phrase — the questions people demonstrably "
                + "ask, which is what answer engines quote."),

            AIFunctionFactory.Create(
                async (string topic) =>
                {
                    if (++calls > settings.MaxToolCalls) return Spent();
                    var answer = await knowledge.AskAsync(userId, $"What do people ask about {topic}?", ct);
                    foreach (var s in answer?.Suggestions ?? []) if (!kbQuestions.Contains(s, StringComparer.OrdinalIgnoreCase)) kbQuestions.Add(s);
                    trace.Add(new AgentStep("knowledge_questions", topic, $"{answer?.Suggestions.Count ?? 0} suggestions"));
                    return answer is null ? "No knowledge base is configured." : answer.ToPromptBlock();
                },
                "knowledge_questions",
                "What the brand's own readers and customers ask about a topic, from its knowledge base."),
        };

        var prompt = BuildPrompt(transcript, campaignName);
        var responseText = string.Empty;
        var success = false;
        try
        {
            var client = await chatProviders.ResolveAsync(userId, "chat", ct);
            using var agent = new ChatClientBuilder(client).UseFunctionInvocation().Build();
            var response = await agent.GetResponseAsync(prompt, new ChatOptions { Tools = tools }, ct);
            responseText = response.Text;
            success = true;
            var plan = AiOrchestrator.ParseModelJson(responseText);
            var result = Assemble(plan, metrics, paaQuestions, kbQuestions, lookups, calls);
            if (result.Keywords.Count == 0)
            {
                logger.LogWarning("SEO research agent produced no keywords; using the fixed pipeline.");
                return await inner.ResearchAsync(userId, transcript, campaignName, ct);
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "SEO research agent failed; using the fixed pipeline.");
            return await inner.ResearchAsync(userId, transcript, campaignName, ct);
        }
        finally
        {
            promptLog.Record(new PromptLogEntry(
                clock.GetUtcNow(), userId, "seo-research-agent", "chat",
                prompt.Length <= 600 ? prompt : prompt[..600] + "…",
                responseText.Length <= 600 ? responseText : responseText[..600] + "…",
                success, stopwatch.ElapsedMilliseconds));
        }
    }

    private static object Compact(SeoKeyword k) => new { k.Term, k.Volume, k.Difficulty, k.Cpc, k.Intent };

    internal static string BuildPrompt(TranscriptContent transcript, string? campaignName) => $$"""
        You are the search strategist for a content campaign. From the source below, build the
        keyword and question plan the writers will target, using the tools to ground every
        choice in real data:
        1. Propose 2-3 seeds (the head term, the product/technology name, the problem solved)
           and expand each with keyword_ideas.
        2. Price your shortlist with keyword_metrics; read serp_snapshot for the 2-3 phrases you
           most want to win to judge intent and whether an AI Overview already answers it.
        3. Pull people_also_ask for the primary phrase and knowledge_questions for the topic.
        4. Stop when you have 8-12 keywords and 6-10 questions you can defend, or when the
           budget is spent.

        Selection rules (SEO + AEO):
        - Every keyword must be something the SOURCE genuinely covers — a term it does not
          address is a target this content cannot win.
        - Mix 2-3 head terms with specific long-tail phrases; prefer phrases with real volume
          and beatable difficulty over vanity terms.
        - Prefer questions with a definite answer in the source (answer engines quote
          one-paragraph answers); mark each question's source: "paa", "knowledge-base" or "model".
        - Note where an AI Overview or featured snippet already occupies a phrase — the content
          must then lead with a quotable direct answer to be cited.

        Reply with JSON only:
        { "primaryKeyword": string,
          "keywords": [ { "term": string, "why": string } ],
          "questions": [ { "question": string, "source": "paa"|"knowledge-base"|"model" } ],
          "notes": [ string ] }

        Campaign: {{campaignName ?? "(unnamed)"}}

        Source:
        {{TranscriptService.ToPromptText(transcript)}}
        """;

    /// <summary>Metrics come from what the tools returned, never from the model's memory.</summary>
    internal static SeoResearchResponse Assemble(
        JsonElement plan,
        IReadOnlyDictionary<string, SeoKeyword> metrics,
        IReadOnlyList<string> paa,
        IReadOnlyList<string> kb,
        IReadOnlySet<string> lookups,
        int toolCalls)
    {
        var keywords = new List<SeoTarget>();
        var primary = plan.TryGetProperty("primaryKeyword", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()?.Trim() : null;
        if (plan.TryGetProperty("keywords", out var kws) && kws.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in kws.EnumerateArray())
            {
                var term = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("term", out var t) ? t.GetString()?.Trim()
                    : item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(term) || keywords.Any(k => k.Term.Equals(term, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                keywords.Add(metrics.TryGetValue(term, out var m)
                    ? new SeoTarget(m.Term, m.Volume, m.Difficulty, Math.Round(DataForSeoProvider.Opportunity(m), 2), "provider", m.Competition, m.Cpc, m.Intent)
                    : new SeoTarget(term, null, null, null, "model"));
            }
        }
        if (primary is not null)
        {
            // The agent's primary leads the list; the SEO targets UI treats position 0 as primary.
            var index = keywords.FindIndex(k => k.Term.Equals(primary, StringComparison.OrdinalIgnoreCase));
            if (index > 0)
            {
                var head = keywords[index];
                keywords.RemoveAt(index);
                keywords.Insert(0, head);
            }
        }

        var questions = new List<SeoQuestion>();
        void Add(string? text, string source)
        {
            var trimmed = text?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) && !questions.Any(q => q.Question.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                questions.Add(new SeoQuestion(trimmed, source));
            }
        }
        if (plan.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in qs.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var q = item.TryGetProperty("question", out var qq) ? qq.GetString() : null;
                var source = item.TryGetProperty("source", out var ss) && ss.ValueKind == JsonValueKind.String ? ss.GetString()! : "model";
                // Only questions the tools actually saw may claim a data source.
                if (source == "paa" && !paa.Contains(q ?? "", StringComparer.OrdinalIgnoreCase)) source = "model";
                if (source == "knowledge-base" && !kb.Contains(q ?? "", StringComparer.OrdinalIgnoreCase)) source = "model";
                Add(q, source);
            }
        }
        foreach (var q in paa) Add(q, "paa");
        foreach (var q in kb) Add(q, "knowledge-base");

        var notes = new List<string> { $"Planned by the research agent in {toolCalls} tool call(s)." };
        if (plan.TryGetProperty("notes", out var ns) && ns.ValueKind == JsonValueKind.Array)
        {
            notes.AddRange(ns.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!.Trim()).Where(n => n.Length > 0).Take(6));
        }
        var hasMetrics = keywords.Any(k => k.Source == "provider");
        if (!hasMetrics)
        {
            notes.Add("No keyword carried provider metrics — the terms are the model's reading of the source.");
        }

        return new SeoResearchResponse(
            [.. keywords.Take(MaxKeywords)],
            [.. questions.OrderBy(q => q.Source switch { "paa" => 0, "knowledge-base" => 1, _ => 2 }).Take(MaxQuestions)],
            hasMetrics,
            notes,
            [.. lookups]);
    }
}
