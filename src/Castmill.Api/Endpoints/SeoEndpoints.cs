using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Api.Services.Seo;
using Castmill.Api.Services.Evidence;
using Castmill.Core.Resources;
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
        // Research runs BEFORE generation and persists nothing: it is a proposal the user
        // edits. /keyword-plan stays as the post-hoc report that creates an artifact.
        group.MapPost("/research", ResearchAsync).Validate<SeoResearchRequest>().RequireRateLimiting("ai");
        group.MapPost("/deep-analysis", DeepAnalysisAsync).Validate<SeoDeepAnalysisRequest>().RequireRateLimiting("ai");

        group.MapGet("/reports/{artifactId:guid}", GetReportAsync);
        group.MapPost("/reports/{artifactId:guid}/share", ShareAsync).RequireRateLimiting("writes");
        group.MapPost("/reports/{artifactId:guid}/angles/regenerate", RegenerateAnglesAsync)
            .RequireRateLimiting("ai");
        return routes;
    }

    /// <summary>
    /// The mandatory pre-production analysis. It persists the keyword/AEO research and a
    /// live SERP snapshot as the campaign's SEO report before any titles or copy are made.
    /// The report remains a draft until the user saves their chosen campaign targets.
    /// </summary>
    private static async Task<IResult> DeepAnalysisAsync(
        SeoDeepAnalysisRequest request,
        System.Security.Claims.ClaimsPrincipal principal,
        ISeoResearch research,
        ISeoProvider provider,
        ISeoReportService reportService,
        IContentDependencyService dependencies,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == request.CampaignId, ct);
        if (campaign is null)
        {
            return Results.NotFound();
        }

        var transcript = await dependencies.LoadApprovedSourceAsync(
            request.CampaignId, request.TranscriptArtifactId, ct);
        if (transcript is null)
        {
            return Results.NotFound();
        }

        var researchContext = string.IsNullOrWhiteSpace(campaign.Brief)
            ? campaign.Name
            : $"{campaign.Name}\nCampaign audience and editorial brief:\n{campaign.Brief}";
        var result = await research.ResearchAsync(
            AuthEndpoints.GetUserId(principal), transcript, researchContext, ct);
        var primary = result.Keywords.Count > 0 ? result.Keywords[0].Term : null;
        if (string.IsNullOrWhiteSpace(primary))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                detail: "The analysis could not identify a grounded search target. Refine the brief or source, then run it again.");
        }

        SeoSerpSnapshot serp;
        if (!provider.IsConfigured)
        {
            serp = new SeoSerpSnapshot(primary, null, null, []);
        }
        else
        {
            try
            {
                serp = await provider.GetSerpSnapshotAsync(primary, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                serp = new SeoSerpSnapshot(primary, null, null, []);
                result = result with
                {
                    Notes = [.. result.Notes,
                        "The live SERP and zero-click answer surfaces were unavailable for this run."]
                };
            }
        }

        var now = clock.GetUtcNow();
        var insights = await reportService.BuildAsync(
            AuthEndpoints.GetUserId(principal), result, serp, request.SiteUrl,
            campaign.Brief, transcript, now, ct);

        var recommendations = new List<string>
        {
            $"Use “{primary}” as the primary topic in the title, opening and first heading.",
            $"Answer {Math.Min(result.Questions.Count, 5)} priority questions in self-contained passages suitable for answer engines.",
            "Keep every channel aligned to the same search intent while changing the hook for its audience.",
        };
        if (serp.OrganicResults.Count > 0)
        {
            recommendations.Add("Differentiate from the live organic leaders listed in this report; do not merely paraphrase their titles.");
        }
        if (serp.AiOverview is not null || serp.FeaturedSnippet is not null)
        {
            recommendations.Add("Lead with a concise, attributable answer that can compete for the existing zero-click answer surface.");
        }
        if (Uri.TryCreate(request.SiteUrl, UriKind.Absolute, out var siteUri))
        {
            var domainRanks = serp.OrganicResults.Any(r =>
                r.Domain.Equals(siteUri.Host, StringComparison.OrdinalIgnoreCase)
                || r.Domain.EndsWith($".{siteUri.Host}", StringComparison.OrdinalIgnoreCase));
            recommendations.Add(domainRanks
                ? $"{siteUri.Host} already appears in this result set; strengthen its topical authority with the campaign cluster."
                : $"{siteUri.Host} is absent from the captured top results; make the source-backed differentiation explicit.");
        }

        if (insights.Aeo.EnginesSucceeded > 0)
        {
            recommendations.Add(insights.Aeo.EnginesCitingDomain == 0
                ? "No available answer engine cited the site. Prioritize definition, comparison, and direct Q&A formats with attributable claims."
                : $"AI-answer visibility is {insights.Aeo.VisibilityPercent:0.#}%; reinforce the formats and sources already earning citations while targeting the missing engines.");
        }
        if (insights.SiteAuthority?.ReferringDomains is { } ownRefs
            && insights.Competitors is { Count: > 1 } competitors)
        {
            var bestRefs = competitors.Where(c => !c.IsOwnDomain)
                .Max(c => c.Authority?.ReferringDomains ?? 0);
            if (bestRefs > ownRefs * 1.5)
            {
                recommendations.Add($"Authority is outmatched ({ownRefs:N0} vs {bestRefs:N0} referring domains). Prefer specific lower-difficulty queries before competing for head terms.");
            }
        }
        if (insights.RankedKeywords.Count > 0)
        {
            recommendations.Add($"Protect and extend {insights.RankedKeywords.Count} existing organic positions instead of publishing duplicate pages for the same intent.");
        }
        if (insights.KeywordGaps.Count > 0)
        {
            recommendations.Add($"The report identified {insights.KeywordGaps.Count} transcript-supported keyword gaps; use the content angles to turn the strongest gaps into assets.");
        }

        var existing = await db.Artifacts
            .Where(a => a.CampaignId == campaign.Id && a.Kind == "seo-report")
            .OrderByDescending(a => a.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        SeoAnalysisReportResponse? previous = null;
        if (existing is not null)
        {
            try
            {
                previous = JsonSerializer.Deserialize<SeoAnalysisReportResponse>(existing.ContentJson, Json);
            }
            catch (JsonException)
            {
            }
        }
        var id = existing?.Id ?? Guid.NewGuid();
        var report = new SeoAnalysisReportResponse(
            id, now, result, serp, recommendations, Status: "Draft",
            SiteUrl: request.SiteUrl, CampaignBrief: campaign.Brief, Insights: insights,
            InputsStale: false, AnglesStale: false,
            ShareStale: previous?.SharedAt is not null, SharedAt: previous?.SharedAt);
        var serializedReport = JsonSerializer.Serialize(report, Json);
        var reportTitle = $"SEO/AEO analysis — {campaign.Name}";
        var artifact = existing ?? new Artifact
        {
            Id = id,
            TenantId = tenant.TenantId!.Value,
            CampaignId = campaign.Id,
            Kind = "seo-report",
            Title = reportTitle,
            ContentJson = serializedReport,
            Version = 1,
            CreatedAt = now,
        };
        if (existing is not null)
        {
            await ArtifactEndpoints.SnapshotRevisionAsync(
                db, existing, "deep-analysis", now, ct);
        }
        artifact.Title = reportTitle;
        artifact.ContentJson = serializedReport;
        artifact.UpdatedAt = now;
        if (existing is null)
        {
            db.Artifacts.Add(artifact);
        }
        else
        {
            artifact.Version++;
        }

        await UpsertPlaceholderBlogAsync(
            campaign,
            insights.ContentAngles.Count > 0 ? insights.ContentAngles[0] : null,
            tenant, db, now, ct);
        await db.SaveChangesAsync(ct);
        await dependencies.CaptureDeepAnalysisAsync(artifact, campaign, ct);

        return existing is null
            ? Results.Created($"/api/v1/seo/reports/{id}", report)
            : Results.Ok(report);
    }

    private static async Task UpsertPlaceholderBlogAsync(
        Campaign campaign, SeoContentAngle? angle, ITenantProvider tenant,
        CastmillDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (angle is null || await db.Artifacts.AnyAsync(
                artifact => artifact.CampaignId == campaign.Id && artifact.Kind == "blog"
                    && !artifact.ContentJson.Contains("\"placeholder\":true"), ct))
        {
            return;
        }

        var content = JsonSerializer.Serialize(new
        {
            markdown = $"# {angle.Angle}\n\n> Placeholder seeded from the strongest approved SEO/AEO opportunity. Use Generate in the Producer to draft it.",
            placeholder = true,
            seedAngle = angle.Angle,
            targetKeyword = angle.TargetKeyword,
            rationale = angle.Rationale,
        }, Json);
        var existing = await db.Artifacts.SingleOrDefaultAsync(
            artifact => artifact.CampaignId == campaign.Id && artifact.Kind == "blog"
                && artifact.ContentJson.Contains("\"placeholder\":true"), ct);
        if (existing is null)
        {
            db.Artifacts.Add(new Artifact
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId!.Value,
                CampaignId = campaign.Id,
                Kind = "blog",
                Title = angle.Angle.Length > 300 ? angle.Angle[..300] : angle.Angle,
                ContentJson = content,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.Title = angle.Angle.Length > 300 ? angle.Angle[..300] : angle.Angle;
            existing.ContentJson = content;
            existing.Version++;
            existing.UpdatedAt = now;
        }
    }

    private static async Task<IResult> RegenerateAnglesAsync(
        Guid artifactId,
        ClaimsPrincipal principal,
        ISeoReportService reportService,
        IContentDependencyService dependencies,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var artifact = await db.Artifacts.SingleOrDefaultAsync(
            row => row.Id == artifactId && row.Kind == "seo-report", ct);
        if (artifact is null)
        {
            return Results.NotFound();
        }
        SeoAnalysisReportResponse report;
        try
        {
            report = JsonSerializer.Deserialize<SeoAnalysisReportResponse>(artifact.ContentJson, Json)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return Results.Problem("The stored SEO/AEO report could not be read.", statusCode: 409);
        }

        var campaign = await db.Campaigns.SingleAsync(row => row.Id == artifact.CampaignId, ct);
        var transcriptArtifactId = await db.Artifacts
            .Where(row => row.CampaignId == campaign.Id && row.Kind == "transcript")
            .OrderByDescending(row => row.CreatedAt)
            .Select(row => (Guid?)row.Id)
            .FirstOrDefaultAsync(ct);
        var transcript = transcriptArtifactId is null
            ? null
            : await dependencies.LoadApprovedTranscriptAsync(
                campaign.Id, transcriptArtifactId.Value, ct);
        if (transcript is null)
        {
            return Results.Problem("This campaign has no readable transcript.", statusCode: 409);
        }

        var angles = await reportService.RegenerateAnglesAsync(
            AuthEndpoints.GetUserId(principal), report, campaign.Brief, transcript, ct);
        var now = clock.GetUtcNow();
        var insights = report.Insights ?? new SeoDeepInsights(
            new SeoAeoScorecard(null, 0, 0, []), [], [], null, [], [], [], now);
        var updated = report with
        {
            Insights = insights with { ContentAngles = angles, AnglesGeneratedAt = now },
            AnglesStale = false,
            ShareStale = report.SharedAt is not null,
        };
        artifact.ContentJson = JsonSerializer.Serialize(updated, Json);
        artifact.Version++;
        artifact.UpdatedAt = now;
        await UpsertPlaceholderBlogAsync(
            campaign, angles.Count > 0 ? angles[0] : null, tenant, db, now, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new SeoAngleRegenerationResponse(artifact.Id, angles, now));
    }

    /// <summary>
    /// Transcript → AI SEO brief (summary, focus keywords, 3 A/B YouTube titles)
    /// → DataForSEO metrics for those keywords + related suggestions → ranked
    /// keyword plan persisted as a "seo-keyword-plan" artifact.
    /// </summary>
    private static async Task<IResult> ResearchAsync(
        SeoResearchRequest request,
        System.Security.Claims.ClaimsPrincipal principal,
        ISeoResearch research,
        IContentDependencyService dependencies,
        CastmillDbContext db,
        CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == request.CampaignId, ct);
        if (campaign is null)
        {
            return Results.NotFound();
        }

        var transcriptArtifact = await db.Artifacts.SingleOrDefaultAsync(
            a => a.Id == request.TranscriptArtifactId
                 && a.CampaignId == request.CampaignId && a.Kind == "transcript", ct);
        var transcript = transcriptArtifact is null
            ? null
            : await dependencies.LoadApprovedTranscriptAsync(
                request.CampaignId, transcriptArtifact.Id, ct);
        if (transcript is null)
        {
            return Results.NotFound();
        }

        try
        {
            return Results.Ok(await research.ResearchAsync(
                AuthEndpoints.GetUserId(principal), transcript, campaign.Name, ct));
        }
        catch (AiNotConfiguredException ex)
        {
            // Same contract as the generation endpoints: a missing credential is a reported
            // condition, not a 500.
            return Results.Problem(ex.Message, statusCode: 409);
        }
    }

    private static async Task<IResult> KeywordPlanAsync(
        KeywordPlanRequest request,
        System.Security.Claims.ClaimsPrincipal principal,
        IAiOrchestrator orchestrator,
        ISeoProvider provider,
        IContentDependencyService dependencies,
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
            : await dependencies.LoadApprovedTranscriptAsync(
                request.CampaignId, transcriptArtifact.Id, ct);
        if (transcript is null)
        {
            return Results.NotFound();
        }

        // 1. Internal AI research pass — the "focus" steer flows into the SEO analysis.
        // This uses the legacy seo-brief generator contract, but it is not a product artifact:
        // the temporary row is removed after its structured result seeds the keyword plan.
        var spec = Generators.Find("seo-brief")!;
        var brief = await orchestrator.RunGeneratorAsync(
            AuthEndpoints.GetUserId(principal), campaign, transcript, request.Focus, spec, ct);
        if (!brief.Success)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
                detail: $"SEO research generation failed: {brief.Error}");
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
        db.Artifacts.Remove(briefArtifact);
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

    // Two shareable SEO shapes exist: analyze reports and keyword plans (roadmap E9.4).
    // The kind check is inlined in each query because EF can't translate a helper.
    private static async Task<IResult> GetReportAsync(Guid artifactId, CastmillDbContext db, CancellationToken ct)
    {
        var artifact = await db.Artifacts.SingleOrDefaultAsync(
            a => a.Id == artifactId && (a.Kind == "seo-report" || a.Kind == "seo-keyword-plan"), ct);
        return artifact is null
            ? Results.NotFound()
            : Results.Text(artifact.ContentJson, "application/json");
    }

    /// <summary>Publishes a static HTML snapshot to the public container — the share link needs no auth.</summary>
    private static async Task<IResult> ShareAsync(
        Guid artifactId,
        IPublicContentStore publicStore,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!publicStore.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Storage is not configured for public publishing.");
        }
        var artifact = await db.Artifacts.SingleOrDefaultAsync(
            a => a.Id == artifactId && (a.Kind == "seo-report" || a.Kind == "seo-keyword-plan"), ct);
        if (artifact is null)
        {
            return Results.NotFound();
        }

        var html = RenderSnapshotHtml(artifact);
        var url = await publicStore.PublishAsync(
            $"seo-shares/{artifact.TenantId}/{artifact.Id}.html",
            Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", ct);
        try
        {
            var report = JsonSerializer.Deserialize<SeoAnalysisReportResponse>(artifact.ContentJson, Json);
            if (report is not null)
            {
                var now = clock.GetUtcNow();
                artifact.ContentJson = JsonSerializer.Serialize(
                    report with { SharedAt = now, ShareStale = false }, Json);
                artifact.Version++;
                artifact.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (JsonException)
        {
        }
        return Results.Ok(new { shareUrl = url.ToString() });
    }

    /// <summary>Self-contained snapshot; every dynamic value is HTML-encoded — report data is user/provider input.</summary>
    internal static string RenderSnapshotHtml(Artifact artifact)
    {
        var encoder = HtmlEncoder.Default;
        using var doc = JsonDocument.Parse(artifact.ContentJson);
        var root = doc.RootElement;
        var serp = root.TryGetProperty("serp", out var serpNode) ? serpNode : default;
        var research = root.TryGetProperty("research", out var researchNode) ? researchNode : default;
        var keyword = root.TryGetProperty("keyword", out var k)
            ? k.GetString() ?? ""
            : serp.ValueKind == JsonValueKind.Object && serp.TryGetProperty("keyword", out var sk)
                ? sk.GetString() ?? ""
                : "";
        var heading = keyword.Length > 0 ? $"SEO report — {keyword}" : artifact.Title;
        // Reports carry a score; keyword plans don't — never invent a 0/100.
        var scoreHtml = root.TryGetProperty("score", out var s)
            ? $"""<p class="score">{s.GetInt32()}/100</p>"""
            : "";
        var summaryHtml = root.TryGetProperty("summary", out var sum) && sum.GetString() is { Length: > 0 } text
            ? $"<p>{encoder.Encode(text)}</p>"
            : "";

        var titles = new StringBuilder();
        if (root.TryGetProperty("youtubeTitles", out var yts) && yts.ValueKind == JsonValueKind.Array)
        {
            foreach (var title in yts.EnumerateArray())
            {
                titles.Append("<li>").Append(encoder.Encode(title.GetString() ?? "")).Append("</li>");
            }
        }
        var titlesHtml = titles.Length > 0 ? $"<h2>YouTube title candidates</h2><ol>{titles}</ol>" : "";

        var rows = new StringBuilder();
        var kws = root.TryGetProperty("keywords", out var directKeywords)
            ? directKeywords
            : research.ValueKind == JsonValueKind.Object && research.TryGetProperty("keywords", out var researchedKeywords)
                ? researchedKeywords
                : default;
        if (kws.ValueKind == JsonValueKind.Array)
        {
            foreach (var kw in kws.EnumerateArray())
            {
                // Metrics can be JSON null in a keyword plan — render a dash, not a fake 0.
                rows.Append("<tr><td>")
                    .Append(encoder.Encode(kw.TryGetProperty("term", out var t) ? t.GetString() ?? "" : ""))
                    .Append("</td><td>")
                    .Append(kw.TryGetProperty("volume", out var v) && v.ValueKind == JsonValueKind.Number
                        ? v.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : "–")
                    .Append("</td><td>")
                    .Append(kw.TryGetProperty("difficulty", out var d) && d.ValueKind == JsonValueKind.Number
                        ? d.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture) : "–")
                    .Append("</td></tr>");
            }
        }

        var angles = new StringBuilder();
        var ang = root.TryGetProperty("contentAngles", out var contentAngles)
            ? contentAngles
            : root.TryGetProperty("recommendations", out var recommendations)
                ? recommendations
                : default;
        if (ang.ValueKind == JsonValueKind.Array)
        {
            foreach (var angle in ang.EnumerateArray())
            {
                angles.Append("<li>").Append(encoder.Encode(angle.GetString() ?? "")).Append("</li>");
            }
        }
        var anglesHtml = angles.Length > 0 ? $"<h2>Content angles</h2><ul>{angles}</ul>" : "";

        return $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex">
            <title>{{encoder.Encode(heading)}}</title>
            <style>body{font-family:Georgia,serif;max-width:720px;margin:3rem auto;padding:0 1rem;color:#2b2b2b}
            table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid #ddd;padding:.5rem;text-align:left}
            .score{font-size:3rem;font-weight:bold}</style></head><body>
            <h1>{{encoder.Encode(heading)}}</h1>
            {{scoreHtml}}
            {{summaryHtml}}
            <h2>Keywords</h2><table><tr><th>Term</th><th>Volume</th><th>Difficulty</th></tr>{{rows}}</table>
            {{titlesHtml}}
            {{anglesHtml}}
            <p><small>Generated by Castmill on {{artifact.UpdatedAt:yyyy-MM-dd}}.</small></p>
            </body></html>
            """;
    }
}
