using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Castmill.Api.Data;
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

public static class SeoEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapSeoEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/seo").RequireAuthorization("TenantAllowed");

        group.MapPost("/analyze", AnalyzeAsync).Validate<SeoAnalyzeRequest>().RequireRateLimiting("searches");
        group.MapGet("/reports/{artifactId:guid}", GetReportAsync);
        group.MapPost("/reports/{artifactId:guid}/share", ShareAsync).RequireRateLimiting("writes");
        return routes;
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
