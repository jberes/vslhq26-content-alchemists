using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Api.Services.Seo;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

public sealed record SeoAnalyzeRequest(
    [property: Required] Guid CampaignId,
    [property: Required, MinLength(2), MaxLength(200)] string Keyword,
    [property: MaxLength(2000), Url] string? TargetUrl);

public sealed record KeywordPlanRequest(
    [property: Required] Guid CampaignId,
    [property: Required] Guid TranscriptArtifactId,
    /// <summary>Optional steer: what the content should focus on ranking for.</summary>
    [property: MaxLength(2000)] string? Focus);

public static class SeoEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapSeoEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/seo").RequireAuthorization("TenantAllowed");

        group.MapPost("/analyze", AnalyzeAsync).Validate<SeoAnalyzeRequest>().RequireRateLimiting("searches");
        group.MapPost("/keyword-plan", KeywordPlanAsync).Validate<KeywordPlanRequest>().RequireRateLimiting("ai");
        group.MapGet("/reports/{artifactId:guid}", GetReportAsync);
        group.MapPost("/reports/{artifactId:guid}/share", ShareAsync).RequireRateLimiting("writes");
        return routes;
    }

    /// <summary>
    /// Transcript → AI SEO brief (summary, focus keywords, 3 A/B YouTube titles)
    /// → DataForSEO metrics for those keywords + related suggestions → ranked
    /// keyword plan persisted as a "seo-keyword-plan" artifact.
    /// </summary>
    private static async Task<IResult> KeywordPlanAsync(
        KeywordPlanRequest request,
        System.Security.Claims.ClaimsPrincipal principal,
        IAiOrchestrator orchestrator,
        ISeoProvider provider,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!provider.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "DataForSEO is not configured. Fill in Seo:ApiKey (base64 login:password).");
        }
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == request.CampaignId, ct);
        if (campaign is null)
        {
            return Results.NotFound();
        }
        var transcriptArtifact = await db.Artifacts.SingleOrDefaultAsync(
            a => a.Id == request.TranscriptArtifactId && a.CampaignId == request.CampaignId && a.Kind == "transcript", ct);
        var transcript = transcriptArtifact is null
            ? null
            : TranscriptService.Parse(transcriptArtifact.ContentJson);
        if (transcript is null)
        {
            return Results.NotFound();
        }

        // 1. AI SEO brief — the "focus" steer flows in as the generation brief.
        var spec = Generators.Find("seo-brief")!;
        var brief = await orchestrator.RunGeneratorAsync(
            AuthEndpoints.GetUserId(principal), campaign, transcript, request.Focus, spec, ct);
        if (!brief.Success)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
                detail: $"SEO brief generation failed: {brief.Error}");
        }
        var briefArtifact = await db.Artifacts.SingleAsync(a => a.Id == brief.ArtifactId, ct);
        using var briefDoc = JsonDocument.Parse(briefArtifact.ContentJson);
        var content = briefDoc.RootElement.GetProperty("content");
        var focusKeywords = content.GetProperty("focusKeywords").EnumerateArray()
            .Where(k => k.ValueKind == JsonValueKind.String)
            .Select(k => k.GetString()!)
            .Take(10)
            .ToList();
        var youtubeTitles = content.GetProperty("youtubeTitles").EnumerateArray()
            .Select(t => t.GetString() ?? "")
            .ToList();
        var summary = content.GetProperty("summary").GetString() ?? "";

        // 2. DataForSEO: exact metrics for the AI's picks + related ideas seeded
        // from the shortest (most head-like) keyword — long-tail seeds rarely
        // have suggestion coverage.
        var metrics = await provider.GetKeywordMetricsAsync(focusKeywords, ct);
        var seed = focusKeywords.OrderBy(k => k.Length).FirstOrDefault();
        var suggestions = seed is null ? [] : await provider.GetSuggestionsAsync(seed, 15, ct);

        // 3. Merge + rank by opportunity (volume vs difficulty).
        var merged = metrics.Select(m => new { keyword = m, source = "ai" })
            .Concat(suggestions.Select(s => new { keyword = s, source = "dataforseo-suggestion" }))
            .GroupBy(x => x.keyword.Term, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(x => DataForSeoProvider.Opportunity(x.keyword))
            .Select(x => new
            {
                x.keyword.Term,
                x.keyword.Volume,
                x.keyword.Difficulty,
                x.keyword.Competition,
                x.keyword.Cpc,
                x.source,
                opportunity = Math.Round(DataForSeoProvider.Opportunity(x.keyword), 2),
            })
            .ToList();

        var now = clock.GetUtcNow();
        var planJson = JsonSerializer.Serialize(new
        {
            summary,
            focus = request.Focus,
            youtubeTitles,
            keywords = merged,
            seoBriefArtifactId = brief.ArtifactId,
            generatedAt = now,
        }, Json);

        var plan = new Artifact
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId!.Value,
            CampaignId = request.CampaignId,
            Kind = "seo-keyword-plan",
            Title = $"Keyword plan — {campaign.Name}",
            ContentJson = planJson,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Artifacts.Add(plan);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/campaigns/{request.CampaignId}/artifacts/{plan.Id}",
            new { planArtifactId = plan.Id, summary, youtubeTitles, keywords = merged });
    }

    private static async Task<IResult> AnalyzeAsync(
        SeoAnalyzeRequest request,
        ISeoProvider provider,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!provider.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "SEO provider is not configured. Fill in Seo:BaseUrl and Seo:ApiKey.");
        }
        if (!await db.Campaigns.AnyAsync(c => c.Id == request.CampaignId, ct))
        {
            return Results.NotFound();
        }

        var analysis = await provider.AnalyzeAsync(request.Keyword, request.TargetUrl, ct);
        var now = clock.GetUtcNow();
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId!.Value,
            CampaignId = request.CampaignId,
            Kind = "seo-report",
            Title = $"SEO — {request.Keyword}",
            ContentJson = JsonSerializer.Serialize(analysis, Json),
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Artifacts.Add(artifact);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/seo/reports/{artifact.Id}", new { reportArtifactId = artifact.Id, analysis.Score });
    }

    private static async Task<IResult> GetReportAsync(Guid artifactId, CastmillDbContext db, CancellationToken ct)
    {
        var artifact = await db.Artifacts.SingleOrDefaultAsync(
            a => a.Id == artifactId && a.Kind == "seo-report", ct);
        return artifact is null
            ? Results.NotFound()
            : Results.Text(artifact.ContentJson, "application/json");
    }

    /// <summary>Publishes a static HTML snapshot to the public container — the share link needs no auth.</summary>
    private static async Task<IResult> ShareAsync(
        Guid artifactId,
        IPublicContentStore publicStore,
        CastmillDbContext db,
        CancellationToken ct)
    {
        if (!publicStore.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Storage is not configured for public publishing.");
        }
        var artifact = await db.Artifacts.SingleOrDefaultAsync(
            a => a.Id == artifactId && a.Kind == "seo-report", ct);
        if (artifact is null)
        {
            return Results.NotFound();
        }

        var html = RenderSnapshotHtml(artifact);
        var url = await publicStore.PublishAsync(
            $"seo-shares/{artifact.TenantId}/{artifact.Id}.html",
            Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", ct);
        return Results.Ok(new { shareUrl = url.ToString() });
    }

    /// <summary>Self-contained snapshot; every dynamic value is HTML-encoded — report data is user/provider input.</summary>
    internal static string RenderSnapshotHtml(Artifact artifact)
    {
        var encoder = HtmlEncoder.Default;
        using var doc = JsonDocument.Parse(artifact.ContentJson);
        var root = doc.RootElement;
        var keyword = root.TryGetProperty("keyword", out var k) ? k.GetString() ?? "" : "";
        var score = root.TryGetProperty("score", out var s) ? s.GetInt32() : 0;

        var rows = new StringBuilder();
        if (root.TryGetProperty("keywords", out var kws) && kws.ValueKind == JsonValueKind.Array)
        {
            foreach (var kw in kws.EnumerateArray())
            {
                rows.Append("<tr><td>")
                    .Append(encoder.Encode(kw.TryGetProperty("term", out var t) ? t.GetString() ?? "" : ""))
                    .Append("</td><td>")
                    .Append(kw.TryGetProperty("volume", out var v) ? v.GetInt64() : 0)
                    .Append("</td><td>")
                    .Append(kw.TryGetProperty("difficulty", out var d) ? d.GetDouble() : 0)
                    .Append("</td></tr>");
            }
        }

        var angles = new StringBuilder();
        if (root.TryGetProperty("contentAngles", out var ang) && ang.ValueKind == JsonValueKind.Array)
        {
            foreach (var angle in ang.EnumerateArray())
            {
                angles.Append("<li>").Append(encoder.Encode(angle.GetString() ?? "")).Append("</li>");
            }
        }

        return $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex">
            <title>SEO report — {{encoder.Encode(keyword)}}</title>
            <style>body{font-family:Georgia,serif;max-width:720px;margin:3rem auto;padding:0 1rem;color:#2b2b2b}
            table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid #ddd;padding:.5rem;text-align:left}
            .score{font-size:3rem;font-weight:bold}</style></head><body>
            <h1>SEO report — {{encoder.Encode(keyword)}}</h1>
            <p class="score">{{score}}/100</p>
            <h2>Keywords</h2><table><tr><th>Term</th><th>Volume</th><th>Difficulty</th></tr>{{rows}}</table>
            <h2>Content angles</h2><ul>{{angles}}</ul>
            <p><small>Generated by Castmill on {{artifact.UpdatedAt:yyyy-MM-dd}}.</small></p>
            </body></html>
            """;
    }
}
