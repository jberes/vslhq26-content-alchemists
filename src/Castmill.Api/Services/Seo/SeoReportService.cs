using System.Text;
using System.Text.Json;
using System.Globalization;
using Castmill.Api.Services.Ai;
using Castmill.Core.Ai;
using Castmill.Core.Resources;

namespace Castmill.Api.Services.Seo;

public interface ISeoReportService
{
    Task<SeoDeepInsights> BuildAsync(
        Guid userId,
        SeoResearchResponse research,
        SeoSerpSnapshot serp,
        string? siteUrl,
        string? audienceAndBrief,
        TranscriptContent transcript,
        DateTimeOffset generatedAt,
        CancellationToken ct);

    Task<IReadOnlyList<SeoContentAngle>> RegenerateAnglesAsync(
        Guid userId,
        SeoAnalysisReportResponse report,
        string? audienceAndBrief,
        TranscriptContent transcript,
        CancellationToken ct);
}

/// <summary>
/// Builds the expensive report tier. Every external section except the already-completed
/// keyword research is soft-fail: a temporarily unavailable backlinks or answer-engine
/// endpoint is represented as an unavailable section, never as a missing report.
/// </summary>
public sealed class SeoReportService(
    ISeoProvider provider,
    IChatProviderRegistry chatProviders,
    ILogger<SeoReportService> logger) : ISeoReportService
{
    private static readonly string[] AnswerEngines = ["chat_gpt", "gemini", "claude", "perplexity"];
    private static readonly SemaphoreSlim VendorConcurrency = new(6, 6);
    private const int CompetitorLimit = 5;

    public async Task<SeoDeepInsights> BuildAsync(
        Guid userId,
        SeoResearchResponse research,
        SeoSerpSnapshot serp,
        string? siteUrl,
        string? audienceAndBrief,
        TranscriptContent transcript,
        DateTimeOffset generatedAt,
        CancellationToken ct)
    {
        var sections = new List<SeoSectionStatus>();
        var domain = DataForSeoProvider.NormalizeDomain(siteUrl);

        IReadOnlyList<SeoRankedKeyword> ranked = [];
        SeoAuthoritySnapshot? authority = null;
        IReadOnlyList<SeoCompetitorSnapshot>? competitors = null;
        SeoAeoScorecard aeo;

        if (!provider.IsConfigured)
        {
            sections.Add(new SeoSectionStatus("Live search data", false,
                "DataForSEO is not configured; model-grounded analysis is still available."));
            aeo = new SeoAeoScorecard(null, 0, 0, []);
        }
        else
        {
            sections.Add(new SeoSectionStatus("Live search data", true,
                $"Captured {serp.OrganicResults.Count} organic results for {serp.Keyword}."));
            sections.Add(new SeoSectionStatus(
                "Keyword datasets",
                research.ProviderLookups is { Count: > 0 },
                research.ProviderLookups is { Count: > 0 } lookups
                    ? $"Completed: {string.Join(", ", lookups)}."
                    : "No DataForSEO keyword dataset completed for this run."));
            if (domain.Length == 0)
            {
                sections.Add(new SeoSectionStatus("Domain intelligence", false,
                    "Add the site URL to measure rankings, authority, competitors, and AI citations."));
                aeo = new SeoAeoScorecard(null, 0, 0, []);
            }
            else
            {
                var rankedTask = SoftAsync(
                    "ranked keywords", () => provider.GetRankedKeywordsAsync(domain, 50, ct),
                    Array.Empty<SeoRankedKeyword>());
                var authorityTask = SoftAsync(
                    "site authority", () => provider.GetAuthorityAsync(domain, ct),
                    default(SeoAuthoritySnapshot));
                var footprintTask = SoftAsync(
                    "site position footprint", () => provider.GetPositionFootprintAsync(domain, ct),
                    default(SeoPositionFootprint));
                var competitorCandidatesTask = SoftAsync(
                    "multi-keyword SERP competitors",
                    () => provider.GetSerpCompetitorsAsync(
                        [.. research.Keywords.Select(keyword => keyword.Term).Take(24)], 12, ct),
                    Array.Empty<SeoCompetitorCandidate>());
                var aeoTask = BuildAeoAsync(domain, audienceAndBrief, serp.Keyword, ct);

                await Task.WhenAll(
                    rankedTask, authorityTask, footprintTask, competitorCandidatesTask, aeoTask);
                ranked = rankedTask.Result;
                authority = authorityTask.Result;
                aeo = aeoTask.Result;
                competitors = await BuildCompetitorsAsync(
                    domain, serp, competitorCandidatesTask.Result,
                    authority, footprintTask.Result, ct);

                sections.Add(new SeoSectionStatus("Ranked keywords", ranked.Count > 0,
                    ranked.Count > 0
                        ? $"Found {ranked.Count} organic keyword positions for {domain}."
                        : $"No ranked-keyword rows were returned for {domain}."));
                sections.Add(new SeoSectionStatus("Authority", authority is not null,
                    authority is null
                        ? "The backlink summary was unavailable."
                        : $"Measured {authority.ReferringDomains?.ToString("N0", CultureInfo.InvariantCulture) ?? "—"} referring domains."));
                sections.Add(new SeoSectionStatus("Competitors", competitors is not null,
                    competitors is null
                        ? "Competitor authority analysis could not be completed."
                        : competitorCandidatesTask.Result.Count > 0
                            ? $"Compared {competitors.Count(c => !c.IsOwnDomain)} domains across {research.Keywords.Count} report keywords."
                            : $"Compared {competitors.Count(c => !c.IsOwnDomain)} domains from the primary live SERP."));
                sections.Add(new SeoSectionStatus("AEO visibility", aeo.EnginesSucceeded > 0,
                    aeo.EnginesSucceeded > 0
                        ? $"{aeo.EnginesCitingDomain} of {aeo.EnginesSucceeded} available engines cited {domain}."
                        : "No answer engine returned a usable response."));
            }
        }

        var rankedTerms = ranked.Select(r => r.Term).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gaps = research.Keywords
            .Where(k => !rankedTerms.Contains(k.Term))
            .OrderByDescending(k => k.Opportunity ?? -1)
            .Take(20)
            .ToList();

        var angles = await GenerateAnglesAsync(
            userId, research, serp, ranked, authority, competitors, aeo,
            audienceAndBrief, transcript, ct);
        sections.Add(new SeoSectionStatus("Content angles", angles.Count > 0,
            angles.Count > 0
                ? $"Generated {angles.Count} report-grounded content opportunities."
                : "No content angles could be generated."));

        return new SeoDeepInsights(
            aeo, gaps, ranked, authority, competitors, angles, sections, generatedAt);
    }

    public Task<IReadOnlyList<SeoContentAngle>> RegenerateAnglesAsync(
        Guid userId,
        SeoAnalysisReportResponse report,
        string? audienceAndBrief,
        TranscriptContent transcript,
        CancellationToken ct)
    {
        var insights = report.Insights;
        return GenerateAnglesAsync(
            userId,
            report.Research,
            report.Serp,
            insights?.RankedKeywords ?? [],
            insights?.SiteAuthority,
            insights?.Competitors,
            insights?.Aeo ?? new SeoAeoScorecard(null, 0, 0, []),
            audienceAndBrief,
            transcript,
            ct);
    }

    private async Task<SeoAeoScorecard> BuildAeoAsync(
        string domain, string? brief, string primaryKeyword, CancellationToken ct)
    {
        var audience = AudienceFrom(brief);
        var question = string.IsNullOrWhiteSpace(audience)
            ? $"What are the best resources and answers for: {primaryKeyword}?"
            : $"For {audience}: what are the best resources and answers for: {primaryKeyword}?";

        var tasks = AnswerEngines.Select(async engine =>
        {
            try
            {
                return await LimitedAsync(
                    () => provider.QueryAnswerEngineAsync(engine, question, domain, ct), ct);
            }
            // A single optional engine must not turn the complete SEO report into a
            // 500. Preserve genuine request cancellation, but record every upstream
            // transport/envelope failure as an honest unavailable-engine row.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Answer-engine query failed for {Engine}.", engine);
                return new SeoAeoEngineResult(
                    engine, EngineLabel(engine), false, false, null, [], "Provider request failed.");
            }
        });

        var engines = await Task.WhenAll(tasks);
        var succeeded = engines.Count(e => e.Succeeded);
        var citing = engines.Count(e => e.Succeeded && e.DomainCited);
        return new SeoAeoScorecard(
            succeeded == 0 ? null : Math.Round(citing * 100.0 / succeeded, 1),
            succeeded, citing, engines);
    }

    private async Task<IReadOnlyList<SeoCompetitorSnapshot>?> BuildCompetitorsAsync(
        string ownDomain,
        SeoSerpSnapshot serp,
        IReadOnlyList<SeoCompetitorCandidate> topicCandidates,
        SeoAuthoritySnapshot? ownAuthority,
        SeoPositionFootprint? ownFootprint,
        CancellationToken ct)
    {
        var discovered = topicCandidates
            .Where(candidate => candidate.Domain.Length > 0)
            .ToList();
        if (discovered.Count == 0)
        {
            discovered = [.. serp.OrganicResults
                .Where(result => !string.IsNullOrWhiteSpace(result.Domain))
                .GroupBy(result => DataForSeoProvider.NormalizeDomain(result.Domain), StringComparer.OrdinalIgnoreCase)
                .Select(group => new SeoCompetitorCandidate(
                    group.Key, null, 1, null, null))];
        }

        var ownTopic = discovered.FirstOrDefault(candidate => SameDomain(candidate.Domain, ownDomain));
        var candidates = discovered
            .Where(candidate => !SameDomain(candidate.Domain, ownDomain))
            .Take(CompetitorLimit)
            .ToList();
        if (candidates.Count == 0)
        {
            return [new SeoCompetitorSnapshot(
                ownDomain,
                BestSerpPosition(ownDomain),
                ownAuthority,
                ownFootprint,
                true,
                ownTopic?.KeywordCount,
                ownTopic?.Visibility,
                ownTopic?.EstimatedTraffic,
                ownTopic?.AveragePosition)];
        }

        var competitorTasks = candidates.Select(async candidate =>
        {
            var authorityTask = SoftAsync(
                $"authority for {candidate.Domain}",
                () => provider.GetAuthorityAsync(candidate.Domain, ct),
                default(SeoAuthoritySnapshot));
            var footprintTask = SoftAsync(
                $"position footprint for {candidate.Domain}",
                () => provider.GetPositionFootprintAsync(candidate.Domain, ct),
                default(SeoPositionFootprint));
            await Task.WhenAll(authorityTask, footprintTask);
            return new SeoCompetitorSnapshot(
                candidate.Domain,
                BestSerpPosition(candidate.Domain),
                authorityTask.Result,
                footprintTask.Result,
                false,
                candidate.KeywordCount,
                candidate.Visibility,
                candidate.EstimatedTraffic,
                candidate.AveragePosition);
        });

        var competitors = (await Task.WhenAll(competitorTasks)).ToList();
        competitors.Insert(0, new SeoCompetitorSnapshot(
            ownDomain,
            BestSerpPosition(ownDomain),
            ownAuthority,
            ownFootprint,
            true,
            ownTopic?.KeywordCount,
            ownTopic?.Visibility,
            ownTopic?.EstimatedTraffic,
            ownTopic?.AveragePosition));
        return competitors;

        int BestSerpPosition(string domain) => serp.OrganicResults
            .Where(result => SameDomain(result.Domain, domain))
            .Select(result => result.Rank)
            .DefaultIfEmpty(0)
            .Min();
    }

    private async Task<IReadOnlyList<SeoContentAngle>> GenerateAnglesAsync(
        Guid userId,
        SeoResearchResponse research,
        SeoSerpSnapshot serp,
        IReadOnlyList<SeoRankedKeyword> ranked,
        SeoAuthoritySnapshot? authority,
        IReadOnlyList<SeoCompetitorSnapshot>? competitors,
        SeoAeoScorecard aeo,
        string? brief,
        TranscriptContent transcript,
        CancellationToken ct)
    {
        var data = new StringBuilder();
        data.AppendLine(CultureInfo.InvariantCulture, $"Primary query: {serp.Keyword}");
        data.AppendLine("Keyword opportunities:");
        foreach (var keyword in research.Keywords.Take(12))
        {
            data.AppendLine(CultureInfo.InvariantCulture,
                $"- {keyword.Term}: volume {keyword.Volume?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, "
                + $"difficulty {keyword.Difficulty?.ToString("0", CultureInfo.InvariantCulture) ?? "unknown"}, intent {keyword.Intent ?? "unknown"}");
        }
        data.AppendLine("Questions people ask:");
        foreach (var question in research.Questions.Take(10))
        {
            data.AppendLine(CultureInfo.InvariantCulture, $"- {question.Question}");
        }
        data.AppendLine("Top organic results — find gaps, do not copy:");
        foreach (var result in serp.OrganicResults.Take(10))
        {
            data.AppendLine(CultureInfo.InvariantCulture, $"- #{result.Rank} {result.Title} ({result.Domain})");
        }
        if (ranked.Count > 0)
        {
            data.AppendLine("The site already ranks for — extend or defend, do not duplicate:");
            foreach (var row in ranked.Take(10))
            {
                data.AppendLine(CultureInfo.InvariantCulture, $"- {row.Term} at position {row.Position}");
            }
        }
        if (authority?.ReferringDomains is { } ownRefs && competitors is { Count: > 0 })
        {
            var best = competitors.Where(c => !c.IsOwnDomain)
                .Max(c => c.Authority?.ReferringDomains ?? 0);
            data.AppendLine(CultureInfo.InvariantCulture,
                $"Authority: site {ownRefs:N0} referring domains; strongest captured competitor {best:N0}.");
        }
        if (aeo.EnginesSucceeded > 0)
        {
            var missingEngines = string.Join(", ",
                aeo.Engines.Where(e => e.Succeeded && !e.DomainCited).Select(e => e.Label));
            data.AppendLine(CultureInfo.InvariantCulture,
                $"AEO visibility: {aeo.VisibilityPercent:0.#}%. Engines not citing the site: {missingEngines}");
        }

        var prompt = $$"""
            Create 4-6 distinct, achievable content angles from this SEO/AEO report.
            Reply with JSON only:
            { "angles": [{ "angle": string, "audienceNeed": string, "suggestedAsset": string,
              "targetKeyword": string, "rationale": string }] }

            Rules:
            - Every angle must be supported by the transcript and name a target keyword.
            - Prefer answer-shaped angles where answer engines do not cite the site.
            - Exploit missing intent or thin competitor coverage; never copy a ranking title.
            - When authority is materially lower than competitors, prefer lower-difficulty long-tail queries.
            - "suggestedAsset" must name a useful format such as blog, comparison, tutorial,
              YouTube explainer, FAQ, or case study—not generic "content".

            Campaign context:
            {{brief ?? "(not supplied)"}}

            Report:
            {{data}}

            Transcript evidence:
            {{TranscriptService.ToPromptText(transcript)}}
            """;

        try
        {
            var client = await chatProviders.ResolveAsync(userId, "chat", ct);
            var response = await client.GetResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, prompt)],
                cancellationToken: ct);
            var root = AiOrchestrator.ParseModelJson(response.Text);
            if (!root.TryGetProperty("angles", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                return FallbackAngles(research);
            }

            var result = rows.EnumerateArray()
                .Select(row => new SeoContentAngle(
                    Text(row, "angle"), Text(row, "audienceNeed"), Text(row, "suggestedAsset"),
                    Text(row, "targetKeyword"), Text(row, "rationale")))
                .Where(a => a.Angle.Length > 0 && a.TargetKeyword.Length > 0)
                .Take(6)
                .ToList();
            return result.Count > 0 ? result : FallbackAngles(research);
        }
        catch (Exception ex) when (ex is JsonException or AiNotConfiguredException or HttpRequestException)
        {
            logger.LogWarning(ex, "Report-grounded content-angle generation failed.");
            return FallbackAngles(research);
        }
    }

    private async Task<T> SoftAsync<T>(string section, Func<Task<T>> action, T fallback)
    {
        try
        {
            return await LimitedAsync(action, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "SEO report section {Section} was unavailable.", section);
            return fallback;
        }
    }

    private static async Task<T> LimitedAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await VendorConcurrency.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            VendorConcurrency.Release();
        }
    }

    private static IReadOnlyList<SeoContentAngle> FallbackAngles(SeoResearchResponse research) =>
        [.. research.Keywords.Take(4).Select((keyword, index) => new SeoContentAngle(
            index == 0 ? $"The definitive answer to {keyword.Term}" : $"A practical guide to {keyword.Term}",
            "A direct, source-backed answer that is easier to use than the current result set.",
            index == 0 ? "Pillar blog with FAQ" : "Tutorial or YouTube explainer",
            keyword.Term,
            "Selected from the highest-opportunity transcript-grounded search targets."))];

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static bool SameDomain(string left, string right)
    {
        left = DataForSeoProvider.NormalizeDomain(left);
        right = DataForSeoProvider.NormalizeDomain(right);
        return left.Equals(right, StringComparison.OrdinalIgnoreCase)
            || left.EndsWith($".{right}", StringComparison.OrdinalIgnoreCase)
            || right.EndsWith($".{left}", StringComparison.OrdinalIgnoreCase);
    }

    private static string? AudienceFrom(string? brief) => brief?.Split('\n')
        .FirstOrDefault(line => line.StartsWith("Audience:", StringComparison.OrdinalIgnoreCase))?
        ["Audience:".Length..].Trim();

    private static string EngineLabel(string engine) => engine switch
    {
        "chat_gpt" => "ChatGPT",
        "gemini" => "Gemini",
        "claude" => "Claude",
        "perplexity" => "Perplexity",
        _ => engine,
    };
}
