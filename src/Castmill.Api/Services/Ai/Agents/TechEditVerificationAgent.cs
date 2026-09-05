using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Castmill.Api.Services.Knowledge;
using Castmill.Core.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Services.Ai.Agents;

/// <summary>One agent action, kept for the producer log and the tests.</summary>
public sealed record AgentStep(string Tool, string Input, string Outcome);

public sealed record VerificationResult(
    IReadOnlyList<ClaimCheck> Claims, IReadOnlyList<AgentStep> Trace, int ToolCalls, bool Ran, string? Error);

public interface ITechEditVerifier
{
    /// <summary>
    /// Checks each technical claim against real sources — the brand's knowledge base, the
    /// pages it cites, the brand's skill files — and returns the same claims with a verdict,
    /// a source and the supporting quote. Never rewrites the artifact.
    /// </summary>
    Task<VerificationResult> VerifyAsync(
        Guid userId, string kind, IReadOnlyList<ClaimCheck> claims, BrandContext brand, CancellationToken ct);
}

/// <summary>
/// The Tech Edit verification agent (ADR-058). A tool-calling loop over three grounded
/// tools; the model decides which claim to check with which tool and when it is done. The
/// output schema is fixed and the artifact is untouched, so the pass-1 validator seam is
/// unchanged — this only turns "flagged unverified" into "verified against the source".
/// </summary>
public sealed class TechEditVerificationAgent(
    IChatProviderRegistry chatProviders,
    IKnowledgeBaseClient knowledge,
    IHttpClientFactory httpClients,
    IOptions<AiOptions> options,
    IPromptLog promptLog,
    TimeProvider clock,
    ILogger<TechEditVerificationAgent> logger) : ITechEditVerifier
{
    public const string HttpClientName = "verifier-fetch";
    private const int MaxPageChars = 6_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<VerificationResult> VerifyAsync(
        Guid userId, string kind, IReadOnlyList<ClaimCheck> claims, BrandContext brand, CancellationToken ct)
    {
        var settings = options.Value.Agents.TechEditVerifier;
        if (!settings.Enabled || claims.Count == 0)
        {
            return new VerificationResult(claims, [], 0, false, null);
        }

        var stopwatch = Stopwatch.StartNew();
        var trace = new List<AgentStep>();
        var calls = 0;
        // Only hosts the run has been pointed at may be fetched: the claims' own sources, the
        // knowledge base's citations, and the brand's context links. The model cannot send
        // this server to an arbitrary URL.
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in claims.Select(c => c.SourceUrl).Where(u => u is not null))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) allowedHosts.Add(uri.Host);
        }
        foreach (var host in HostsIn(brand.CampaignContextBlock)) allowedHosts.Add(host);

        string Spent() => "Tool budget spent — conclude with what you have verified so far.";

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                async (string question) =>
                {
                    if (++calls > settings.MaxToolCalls) return Spent();
                    var answer = await knowledge.AskAsync(userId, question, brand.Knowledge, ct);
                    foreach (var cite in answer?.Citations ?? [])
                    {
                        if (Uri.TryCreate(cite.Url, UriKind.Absolute, out var uri)) allowedHosts.Add(uri.Host);
                    }
                    trace.Add(new AgentStep("ask_knowledge_base", question,
                        answer is null ? "no answer" : $"{answer.Citations.Count} citation(s)"));
                    return answer is null
                        ? "The knowledge base returned nothing for that question."
                        : answer.ToPromptBlock();
                },
                "ask_knowledge_base",
                "Ask the brand's knowledge base (product docs, support cases, published articles) a "
                + "precise question about ONE claim. Returns an answer with source URLs you may then fetch."),

            AIFunctionFactory.Create(
                async (string url) =>
                {
                    if (++calls > settings.MaxToolCalls) return Spent();
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    {
                        trace.Add(new AgentStep("fetch_source", url, "refused: not https"));
                        return "Refused: only https URLs can be fetched.";
                    }
                    if (!allowedHosts.Contains(uri.Host))
                    {
                        trace.Add(new AgentStep("fetch_source", url, "refused: host not cited"));
                        return $"Refused: {uri.Host} was not cited by a claim, the knowledge base or the brand's links.";
                    }
                    try
                    {
                        using var response = await httpClients.CreateClient(HttpClientName).GetAsync(uri, ct);
                        if (!response.IsSuccessStatusCode)
                        {
                            trace.Add(new AgentStep("fetch_source", url, $"HTTP {(int)response.StatusCode}"));
                            return $"The page answered HTTP {(int)response.StatusCode}.";
                        }
                        var text = ToText(await response.Content.ReadAsStringAsync(ct));
                        trace.Add(new AgentStep("fetch_source", url, $"{text.Length} chars"));
                        return text.Length == 0 ? "The page had no readable text." : text;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                    {
                        trace.Add(new AgentStep("fetch_source", url, ex.GetType().Name));
                        return "The page could not be fetched.";
                    }
                },
                "fetch_source",
                "Fetch the readable text of a cited https page so you can quote the sentence that "
                + "supports (or contradicts) a claim. Only pages cited by a claim, the knowledge base, "
                + "or the brand's own links are allowed."),

            AIFunctionFactory.Create(
                (string term) =>
                {
                    if (++calls > settings.MaxToolCalls) return Spent();
                    var hits = SearchSkills(brand, kind, term);
                    trace.Add(new AgentStep("search_skills", term, $"{hits.Count} hit(s)"));
                    return hits.Count == 0
                        ? "No skill file mentions that."
                        : string.Join("\n---\n", hits);
                },
                "search_skills",
                "Search the brand's skill files (authoritative product knowledge written by the brand) "
                + "for an exact API name, option, version or phrase."),
        };

        var prompt = BuildPrompt(kind, claims, brand);
        var responseText = string.Empty;
        var success = false;
        try
        {
            var client = await chatProviders.ResolveAsync(userId, FoundryClientFactory.TechEditAlias, ct);
            using var agent = new ChatClientBuilder(client).UseFunctionInvocation().Build();
            var response = await agent.GetResponseAsync(prompt, new ChatOptions { Tools = tools }, ct);
            responseText = response.Text;
            success = true;
            var verified = Merge(claims, AiOrchestrator.ParseModelJson(responseText));
            return new VerificationResult(verified, trace, calls, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Tech Edit verification agent failed");
            return new VerificationResult(claims, trace, calls, true,
                ex is AiNotConfiguredException ? ex.Message : $"Verifier failed: {ex.GetType().Name}");
        }
        finally
        {
            promptLog.Record(new PromptLogEntry(
                clock.GetUtcNow(), userId, $"{kind}-verify", FoundryClientFactory.TechEditAlias,
                Excerpt(prompt), Excerpt(responseText), success, stopwatch.ElapsedMilliseconds));
        }
    }

    internal static string BuildPrompt(string kind, IReadOnlyList<ClaimCheck> claims, BrandContext brand)
    {
        var text = new StringBuilder();
        var header = $$"""
            You are the fact-checker for a {{kind}} draft. Below are the technical claims the
            editor asserted. For EACH claim, establish whether a real source supports it:
            - ask_knowledge_base for the product fact, then fetch_source the URL it cites and
              find the sentence that supports the claim;
            - search_skills for exact API names, options and versions;
            - a claim is VERIFIED only when you can quote a sentence from a fetched page, a
              knowledge-base answer, or a skill file that supports it. Otherwise it stays
              unverified — never infer, never assume.
            Stop as soon as every claim has a verdict, or when told the tool budget is spent.

            Reply with JSON only:
            { "claims": [ { "statement": string, "verified": boolean, "sourceUrl": string|null,
                            "quote": string|null, "note": string|null } ] }
            Keep every statement text exactly as given so it can be matched.

            Claims:
            """;
        text.AppendLine(header);
        for (var i = 0; i < claims.Count; i++)
        {
            text.Append((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(". ").Append(claims[i].Statement);
            if (claims[i].SourceUrl is { } url) text.Append("  (proposed source: ").Append(url).Append(')');
            text.AppendLine();
        }
        if (brand.Skills is { Count: > 0 })
        {
            text.AppendLine();
            text.Append("Skill files available: ").AppendLine(string.Join(", ", brand.Skills.Select(s => s.Name)));
        }
        return text.ToString();
    }

    /// <summary>Verdicts matched to the original claims by statement; unmatched claims keep their prior state.</summary>
    internal static IReadOnlyList<ClaimCheck> Merge(IReadOnlyList<ClaimCheck> original, JsonElement parsed)
    {
        if (!parsed.TryGetProperty("claims", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return original;
        }
        var verdicts = array.EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("statement", out _))
            .ToList();
        var result = new List<ClaimCheck>(original.Count);
        foreach (var claim in original)
        {
            var verdict = verdicts.FirstOrDefault(v =>
                string.Equals(v.GetProperty("statement").GetString()?.Trim(), claim.Statement.Trim(), StringComparison.OrdinalIgnoreCase));
            if (verdict.ValueKind != JsonValueKind.Object)
            {
                result.Add(claim);
                continue;
            }
            var source = verdict.TryGetProperty("sourceUrl", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var quote = verdict.TryGetProperty("quote", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null;
            var says = verdict.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True;
            var httpsSource = Uri.TryCreate(source, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
            // The policy, not the model, decides "verified": a verdict needs a quote AND a source.
            var verified = says && !string.IsNullOrWhiteSpace(quote) && httpsSource;
            result.Add(new ClaimCheck(claim.Statement, httpsSource ? source : claim.SourceUrl, verified, string.IsNullOrWhiteSpace(quote) ? null : quote!.Trim()));
        }
        return result;
    }

    internal static List<string> SearchSkills(BrandContext brand, string kind, string term)
    {
        var hits = new List<string>();
        if (string.IsNullOrWhiteSpace(term))
        {
            return hits;
        }
        foreach (var skill in (brand.Skills ?? []).Where(s => s.AppliesToKind(kind)))
        {
            var lines = skill.Content.Split('\n');
            for (var i = 0; i < lines.Length && hits.Count < 12; i++)
            {
                if (lines[i].Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    var from = Math.Max(0, i - 1);
                    var to = Math.Min(lines.Length - 1, i + 2);
                    hits.Add($"[{skill.Name}] " + string.Join("\n", lines[from..(to + 1)]).Trim());
                }
            }
        }
        return hits;
    }

    /// <summary>HTML → readable text: scripts and styles dropped, tags removed, whitespace folded, capped.</summary>
    internal static string ToText(string html)
    {
        var text = Regex.Replace(html, @"<(script|style|noscript|svg)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<br\s*/?>|</p>|</h[1-6]>|</li>|</tr>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n\s*\n+", "\n").Trim();
        return text.Length <= MaxPageChars ? text : text[..MaxPageChars];
    }

    private static IEnumerable<string> HostsIn(string? block)
    {
        if (string.IsNullOrWhiteSpace(block)) yield break;
        foreach (Match m in Regex.Matches(block, @"https?://[^\s)""'>]+"))
        {
            if (Uri.TryCreate(m.Value, UriKind.Absolute, out var uri)) yield return uri.Host;
        }
    }

    private static string Excerpt(string text) => text.Length <= 600 ? text : text[..600] + "…";
}
