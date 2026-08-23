using Castmill.Core.Resources;
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
    Guid? SeoBriefArtifactId,
    DateTimeOffset GeneratedAt);

public sealed record ShareResult([property: JsonPropertyName("shareUrl")] string ShareUrl);

/// <summary>Typed client for <c>/api/v1/seo</c> — the desk's data (roadmap E9).</summary>
public sealed class SeoClient(ApiClient api)
{
    /// <summary>
    /// Runs the two-leg plan: AI SEO brief from the transcript, then DataForSEO metrics and
    /// suggestions, merged and ranked by opportunity. Live provider — takes ~10-20 s.
    /// </summary>
    /// <summary>
    /// Keyword + question research BEFORE generation. Persists nothing — the result is a
    /// proposal the user edits while reviewing the deep report.
    /// </summary>
    public Task<SeoResearchResponse> ResearchAsync(
        Guid campaignId, Guid transcriptArtifactId, CancellationToken ct = default) =>
        api.PostAsync<SeoResearchRequest, SeoResearchResponse>(
            "api/v1/seo/research", new SeoResearchRequest(campaignId, transcriptArtifactId),
            anonymous: false, ct);

    public Task<SeoAnalysisReportResponse> DeepAnalysisAsync(
        Guid campaignId, Guid? transcriptArtifactId, string? siteUrl = null,
        CancellationToken ct = default) =>
        api.PostAsync<SeoDeepAnalysisRequest, SeoAnalysisReportResponse>(
            "api/v1/seo/deep-analysis",
            new SeoDeepAnalysisRequest(campaignId, transcriptArtifactId, siteUrl),
            anonymous: false, ct);

    public Task<SeoTargetsResponse> GetTargetsAsync(Guid campaignId, CancellationToken ct = default) =>
        api.GetAsync<SeoTargetsResponse>($"api/v1/campaigns/{campaignId}/seo-targets", ct);

    public Task<SeoAnalysisReportResponse> GetReportAsync(
        Guid artifactId, CancellationToken ct = default) =>
        api.GetAsync<SeoAnalysisReportResponse>($"api/v1/seo/reports/{artifactId}", ct);

    public Task<SeoTargetsResponse> SaveTargetsAsync(
        Guid campaignId, SeoTargetsRequest request, CancellationToken ct = default) =>
        api.PutAsync<SeoTargetsRequest, SeoTargetsResponse>(
            $"api/v1/campaigns/{campaignId}/seo-targets", request, etag: null, ct);

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

    public Task<SeoAngleRegenerationResponse> RegenerateAnglesAsync(
        Guid artifactId, CancellationToken ct = default) =>
        api.PostAsync<object, SeoAngleRegenerationResponse>(
            $"api/v1/seo/reports/{artifactId}/angles/regenerate", new { }, anonymous: false, ct);

    public Task<ContentImpactReviewResponse> GetImpactReviewAsync(
        Guid campaignId, CancellationToken ct = default) =>
        api.GetAsync<ContentImpactReviewResponse>(
            $"api/v1/campaigns/{campaignId}/impact-review", ct);

    public Task<ContentImpactActionResponse> KeepImpactAsync(
        Guid campaignId, Guid artifactId, CancellationToken ct = default) =>
        api.PostAsync<object, ContentImpactActionResponse>(
            $"api/v1/campaigns/{campaignId}/impact-review/{artifactId}/keep",
            new { }, anonymous: false, ct);

    public Task<ContentImpactActionResponse> RegenerateImpactAsync(
        Guid campaignId, Guid artifactId, CancellationToken ct = default) =>
        api.PostAsync<object, ContentImpactActionResponse>(
            $"api/v1/campaigns/{campaignId}/impact-review/{artifactId}/regenerate",
            new { }, anonymous: false, ct);
}
