using Castmill.Core.Resources;

namespace Castmill.Api.Services.Seo;

public interface ISeoDistributionService
{
    Task<SeoDistributionReport> BuildAsync(string url, string? mentionQuery, CancellationToken ct);
}

/// <summary>
/// The post-publish half of the SEO desk (ADR-059): a live crawl of the page the report was
/// written for, judged against the AEO rules the writers were given, plus who is already
/// linking to it and where copies of it are appearing. Every dataset is optional — a missing
/// one becomes a section note, never a failure.
/// </summary>
public sealed class SeoDistributionService(
    ISeoProvider provider,
    TimeProvider clock,
    ILogger<SeoDistributionService> logger) : ISeoDistributionService
{
    /// <summary>Long-form pieces below this are rarely complete answers; matches the blog generator's floor.</summary>
    internal const int MinimumWords = 800;

    public async Task<SeoDistributionReport> BuildAsync(string url, string? mentionQuery, CancellationToken ct)
    {
        var sections = new List<SeoSectionStatus>();
        var now = clock.GetUtcNow();
        if (!provider.IsConfigured)
        {
            sections.Add(new SeoSectionStatus("Live page data", false,
                "DataForSEO is not configured; the page cannot be crawled or its links measured."));
            return new SeoDistributionReport(url, now, null, [], null, [], mentionQuery, null, [], sections);
        }

        var pageTask = SoftAsync("page crawl", () => provider.GetPageSnapshotAsync(url, ct), default(SeoPageSnapshot));
        var linksTask = SoftAsync("referring domains", () => provider.GetReferringDomainsAsync(url, 25, ct),
            new SeoReferringDomainsResult(0, []));
        await Task.WhenAll(pageTask, linksTask);
        var page = pageTask.Result;
        var links = linksTask.Result;

        // Copies of a piece reuse its title far more reliably than its URL, so the title is the
        // default mention query; the producer can override it with the brand or a phrase.
        var query = string.IsNullOrWhiteSpace(mentionQuery) ? page?.Title : mentionQuery.Trim();
        var mentions = new SeoMentionsResult(0, []);
        if (!string.IsNullOrWhiteSpace(query))
        {
            mentions = await SoftAsync("mentions", () => provider.SearchMentionsAsync(query!, 25, ct), mentions);
        }

        sections.Add(new SeoSectionStatus("Page crawl", page is not null,
            page is null ? "The page could not be crawled." : $"HTTP {page.StatusCode}, {page.WordCount:N0} words, on-page score {page.OnPageScore?.ToString("0", System.Globalization.CultureInfo.InvariantCulture) ?? "—"}."));
        sections.Add(new SeoSectionStatus("Referring domains", linksTask.IsCompletedSuccessfully,
            $"{links.TotalCount:N0} domains link to this page."));
        sections.Add(new SeoSectionStatus("Mentions", query is not null,
            query is null ? "No title or phrase to search mentions for." : $"{mentions.TotalCount:N0} pages mention “{query}”."));

        return new SeoDistributionReport(
            url, now, page, Evaluate(url, page), links.TotalCount, links.Items,
            query, query is null ? null : mentions.TotalCount, mentions.Items, sections);
    }

    /// <summary>The AEO rules the writers were given, checked against what actually shipped.</summary>
    internal static IReadOnlyList<SeoPageCheck> Evaluate(string url, SeoPageSnapshot? page)
    {
        if (page is null)
        {
            return [new SeoPageCheck("Page reachable", false, "The crawler got no page back — check the URL and that it is publicly readable.")];
        }
        var reachable = page.StatusCode is >= 200 and < 300;
        var checks = new List<SeoPageCheck>
        {
            new("Page reachable", reachable, reachable
                ? $"HTTP {page.StatusCode}."
                : page.StatusCode is 403 or 503
                    ? $"HTTP {page.StatusCode} — the site refused the crawler (bot protection). Open the page yourself; the checks below could not run."
                    : $"HTTP {page.StatusCode}."),
            new("Served over HTTPS", page.IsHttps, page.IsHttps ? "Yes." : "The page is served over plain HTTP."),
        };

        if (!reachable)
        {
            return checks;
        }

        if (string.IsNullOrWhiteSpace(page.Canonical))
        {
            checks.Add(new("Canonical points here", false,
                "No canonical tag. A Medium or LinkedIn copy indexed first can become the canonical in Google's eyes."));
        }
        else
        {
            var self = SameResource(page.Canonical, url);
            checks.Add(new("Canonical points here", self,
                self ? "The canonical is this page." : $"The canonical points at {page.Canonical} — this page is treated as the copy."));
        }

        checks.Add(new("Exactly one H1", page.H1.Count == 1,
            page.H1.Count == 0 ? "No H1 found." : page.H1.Count == 1 ? page.H1[0] : $"{page.H1.Count} H1 tags — answer engines pick one at random."));

        var questions = page.H2.Concat(page.H3).Count(h => h.TrimEnd().EndsWith('?'));
        checks.Add(new("Question headings", questions > 0,
            questions > 0 ? $"{questions} heading{(questions == 1 ? "" : "s")} phrased as a question." : "No H2/H3 is phrased as a question — the priority questions should head their own sections."));

        checks.Add(new("Section structure", page.H2.Count >= 3,
            $"{page.H2.Count} H2 sections."));

        checks.Add(new("Enough depth", page.WordCount >= MinimumWords,
            $"{page.WordCount:N0} words" + (page.WordCount >= MinimumWords ? "." : $" — under the {MinimumWords}-word floor for a complete answer.")));

        checks.Add(new("Structured data", page.HasStructuredData,
            page.HasStructuredData ? "Schema markup detected." : "No schema markup — add Article plus FAQPage for the FAQ section."));

        var titleLength = page.Title?.Trim().Length ?? 0;
        checks.Add(new("Title length", titleLength is > 0 and <= 60,
            titleLength == 0 ? "No title." : $"{titleLength} characters" + (titleLength <= 60 ? "." : " — over 60, Google will truncate it.")));

        var descriptionLength = page.Description?.Trim().Length ?? 0;
        checks.Add(new("Meta description", descriptionLength is > 0 and <= 160,
            descriptionLength == 0 ? "No meta description." : $"{descriptionLength} characters" + (descriptionLength <= 160 ? "." : " — over 160, it will be cut.")));

        return checks;
    }

    private static bool SameResource(string a, string b)
    {
        static string Key(string value)
        {
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            {
                return value.Trim().TrimEnd('/').ToLowerInvariant();
            }
            var host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal))
            {
                host = host[4..];
            }
            return host + uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        }
        return Key(a) == Key(b);
    }

    private async Task<T> SoftAsync<T>(string dataset, Func<Task<T>> action, T fallback)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Distribution check: {Dataset} unavailable.", dataset);
            return fallback;
        }
    }
}
