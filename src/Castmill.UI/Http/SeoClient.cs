using System.Text.Json.Serialization;

namespace Castmill.UI.Http;

/// <summary>One ranked keyword from the plan (DataForSEO metrics + AI picks, merged).</summary>
public sealed record KeywordRow(
    string Term,
    long? Volume,
    double? Difficulty,
    double? Competition,
    double? Cpc,
    string Source,
    double Opportunity);

/// <summary>The seo-keyword-plan artifact's payload.</summary>
public sealed record KeywordPlan(
    string Summary,
    string? Focus,
    IReadOnlyList<string> YoutubeTitles,
    IReadOnlyList<KeywordRow> Keywords,
    Guid SeoBriefArtifactId,
    DateTimeOffset GeneratedAt);

public sealed record ShareResult([property: JsonPropertyName("shareUrl")] string ShareUrl);

/// <summary>Typed client for <c>/api/v1/seo</c> — the desk's data (roadmap E9).</summary>
public sealed class SeoClient(ApiClient api)
{
    /// <summary>
    /// Runs the two-leg plan: AI SEO brief from the transcript, then DataForSEO metrics and
    /// suggestions, merged and ranked by opportunity. Live provider — takes ~10-20 s.
    /// </summary>
    public Task<object> CreateKeywordPlanAsync(
        Guid campaignId, Guid transcriptArtifactId, string? focus, CancellationToken ct = default) =>
        api.PostAsync<object, object>(
            "api/v1/seo/keyword-plan",
            new { campaignId, transcriptArtifactId, focus },
            anonymous: false,
            ct);

    public Task<ShareResult> ShareReportAsync(Guid artifactId, CancellationToken ct = default) =>
        api.PostAsync<object, ShareResult>(
            $"api/v1/seo/reports/{artifactId}/share", new { }, anonymous: false, ct);
}
