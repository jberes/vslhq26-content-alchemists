using System.Text.Json;
using System.Security.Claims;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Tenancy;
using Castmill.Api.Services.Evidence;
using Castmill.Core;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

public static class CampaignEndpoints
{
    private static readonly string[] DashboardHiddenArtifactKinds =
        ["transcript", "image-prompts", "thumbnail-concepts", "seo-brief", "seo-keyword-plan", "seo-report"];
    public static IEndpointRouteBuilder MapCampaignEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/campaigns").RequireAuthorization("TenantAllowed");

        group.MapGet("/", ListAsync);
        // The whole workspace dashboard in one query set — the front page and the
        // campaigns index used to fetch a full preview per campaign to derive this.
        group.MapGet("/dashboard", DashboardAsync);
        group.MapGet("/review-desk", ReviewDeskAsync);
        group.MapGet("/{id:guid}", GetAsync);
        // One call feeds the campaign header counter, the front page's "slots
        // waiting" block and Focus Mode's slot list (G9) — no per-surface polling.
        group.MapGet("/{id:guid}/preview", PreviewAsync);
        group.MapPost("/", CreateAsync).Validate<CampaignCreateRequest>().RequireRateLimiting("writes");
        group.MapPut("/{id:guid}", UpdateAsync).Validate<CampaignUpdateRequest>().RequireRateLimiting("writes");

        // The chosen SEO/AEO targets. Separate from the campaign PUT because they are written
        // by a different step, by a different decision, and read by every generator.
        group.MapGet("/{id:guid}/seo-targets", GetSeoTargetsAsync);
        group.MapPut("/{id:guid}/seo-targets", SetSeoTargetsAsync)
            .Validate<SeoTargetsRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireRateLimiting("writes");
        return routes;
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    internal static CampaignResponse ToResponse(Campaign c) =>
        new(c.Id, c.OwnerId, c.Name, c.Brief, c.CreatedAt, c.UpdatedAt, c.BrandId,
            ParseLinks(c.ContextJson), c.Status, c.ContentType, c.Intent,
            ParseOutputRecipe(c.OutputRecipeJson), c.SkipSeoAnalysis);

    private static readonly JsonSerializerOptions TargetsJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Stored JSON that predates the schema, or was hand-edited, must read back as "no
    /// targets" rather than 500 — the same forgiving contract ParseLinks already uses.
    /// </summary>
    internal static SeoTargetsResponse ParseSeoTargets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SeoTargetsResponse(null, [], []);
        }

        try
        {
            return JsonSerializer.Deserialize<SeoTargetsResponse>(json, TargetsJson)
                   ?? new SeoTargetsResponse(null, [], []);
        }
        catch (JsonException)
        {
            return new SeoTargetsResponse(null, [], []);
        }
    }

    internal static IReadOnlyList<string> ParseOutputRecipe(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IResult? ValidateRunPlan(string? intent, IReadOnlyList<string>? recipe)
    {
        if (!CampaignIntent.IsValid(intent))
        {
            return Results.Problem("Campaign intent is not supported.", statusCode: 400);
        }
        var invalid = (recipe ?? [])
            .Where(kind => kind != "social" && !ArtifactKinds.IsUserContent(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return invalid.Count == 0
            ? null
            : Results.Problem(
                $"Output recipe contains unsupported kinds: {string.Join(", ", invalid)}.",
                statusCode: 400);
    }

    private static async Task<IResult> GetSeoTargetsAsync(
        Guid id, CastmillDbContext db, CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == id, ct);
        return campaign is null
            ? Results.NotFound()
            : Results.Ok(ParseSeoTargets(campaign.SeoTargetsJson));
    }

    private static async Task<IResult> SetSeoTargetsAsync(
        Guid id,
        SeoTargetsRequest request,
        IContentDependencyService dependencies,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
        {
            return Results.NotFound();
        }

        var hadApprovedTargets = !string.IsNullOrWhiteSpace(campaign.SeoTargetsJson);
        var previousTargetsJson = campaign.SeoTargetsJson;
        var keywords = request.Keywords ?? [];
        var questions = request.Questions ?? [];

        // The primary must be one of the chosen keywords, or the steering block would name a
        // target the rest of the brief never mentions.
        var primary = string.IsNullOrWhiteSpace(request.PrimaryKeyword)
            ? (keywords.Count > 0 ? keywords[0].Term : null)
            : request.PrimaryKeyword.Trim();

        if (primary is not null
            && !keywords.Any(k => string.Equals(k.Term, primary, StringComparison.OrdinalIgnoreCase)))
        {
            keywords = [new SeoTarget(primary), .. keywords];
        }

        var stored = new SeoTargetsResponse(primary, keywords, questions);
        campaign.SeoTargetsJson = keywords.Count == 0 && questions.Count == 0
            ? null   // clearing is a real action, not an empty object
            : JsonSerializer.Serialize(stored, TargetsJson);
        var now = clock.GetUtcNow();
        campaign.UpdatedAt = now;

        // Target approval is the report's review action. Persist it on the report itself so
        // reopening the SEO desk says Approved rather than showing a permanent Draft badge
        // beside content that has already been generated from it.
        var reportArtifact = await db.Artifacts
            .Where(a => a.CampaignId == campaign.Id && a.Kind == "seo-report")
            .OrderByDescending(a => a.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        var strategyApproved = false;
        if (reportArtifact is not null)
        {
            try
            {
                var report = JsonSerializer.Deserialize<SeoAnalysisReportResponse>(
                    reportArtifact.ContentJson, TargetsJson);
                if (report is not null)
                {
                    var approved = keywords.Count > 0;
                    reportArtifact.ContentJson = JsonSerializer.Serialize(
                        report with
                        {
                            Status = approved ? "Approved" : "Draft",
                            AnglesStale = report.AnglesStale || (hadApprovedTargets
                                && !string.Equals(previousTargetsJson, campaign.SeoTargetsJson,
                                    StringComparison.Ordinal)),
                            ShareStale = report.ShareStale || report.SharedAt is not null,
                        }, TargetsJson);
                    reportArtifact.Status = approved ? ArtifactStatus.InReview : ArtifactStatus.Draft;
                    reportArtifact.Version++;
                    reportArtifact.UpdatedAt = now;
                    strategyApproved = approved;
                }
            }
            catch (JsonException)
            {
                // Legacy/unreadable reports remain untouched; the selected campaign targets
                // are still the generation gate and must not be lost because display metadata
                // could not be upgraded.
            }
        }
        await db.SaveChangesAsync(ct);
        if (strategyApproved && reportArtifact is not null)
        {
            await dependencies.CaptureStrategyApprovalAsync(reportArtifact, campaign, ct);
        }

        return Results.Ok(stored);
    }

    internal static IReadOnlyList<CampaignLink>? ParseLinks(string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<CampaignLink>>(contextJson, Json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Validates the brand/links half of a create or update; null means valid.</summary>
    private static async Task<IResult?> ValidateBrandAndLinksAsync(
        Guid? brandId, IReadOnlyList<CampaignLink>? links, CastmillDbContext db, CancellationToken ct,
        string? status = null, string? contentType = null)
    {
        if (links is { Count: > 10 })
        {
            return Results.Problem("A campaign holds at most 10 context links.", statusCode: 400);
        }

        // The tenant filter makes a foreign brand indistinguishable from a missing one.
        if (brandId is { } id && !await db.BrandProfiles.AnyAsync(b => b.Id == id, ct))
        {
            return Results.Problem("That brand does not exist.", statusCode: 400);
        }

        if (status is not null && !CampaignStatus.IsValid(status))
        {
            return Results.Problem("Campaign status must be Draft or Ready.", statusCode: 400);
        }
        if (!CampaignContentType.IsValid(contentType))
        {
            return Results.Problem(
                "Content type must be Tutorial, ProductDemo, Webinar, or ThoughtLeadership.",
                statusCode: 400);
        }

        return null;
    }

    private static async Task<IResult> ListAsync(CastmillDbContext db, CancellationToken ct)
    {
        var campaigns = await db.Campaigns
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);
        return Results.Ok(campaigns.Select(ToResponse).ToList());
    }

    private static async Task<IResult> GetAsync(Guid id, CastmillDbContext db, CancellationToken ct)
    {
        // The tenant query filter makes another tenant's campaign a plain 404 —
        // indistinguishable from "does not exist", so nothing leaks.
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == id, ct);
        return campaign is null ? Results.NotFound() : Results.Ok(ToResponse(campaign));
    }

    /// <summary>
    /// Campaign + artifact previews + image-slot state in one payload (ADR-003 keeps
    /// the heavy content out). This is what every image counter reads.
    /// </summary>
    private static async Task<IResult> PreviewAsync(Guid id, CastmillDbContext db, CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
        {
            return Results.NotFound();
        }
        var artifactRows = await db.Artifacts
            .Where(a => a.CampaignId == id)
            .OrderBy(a => a.Kind).ThenBy(a => a.CreatedAt)
            .Select(a => new { a.Id, a.CampaignId, a.ParentArtifactId, a.Kind, a.Title, a.Status, a.Version, a.CreatedAt, a.UpdatedAt, a.CitationsJson, a.ContentJson })
            .ToListAsync(ct);
        var evidence = await ArtifactEndpoints.LoadEvidenceMarkersAsync(
            db, artifactRows.Select(row => row.Id).ToList(), ct);
        var artifacts = artifactRows
            .Select(a => new ArtifactPreviewResponse(
                a.Id, a.CampaignId, a.Kind, a.Title, a.Status, a.Version, a.CreatedAt, a.UpdatedAt,
                ArtifactEndpoints.ParseCitations(a.CitationsJson), a.ParentArtifactId,
                a.ContentJson.Contains("\"placeholder\":true"),
                evidence.GetValueOrDefault(a.Id)))
            .ToList();
        var slots = await db.ImageSlots.Where(s => s.CampaignId == id).ToListAsync(ct);
        var sources = await db.SourceAssets
            .Where(source => source.CampaignId == id)
            .OrderBy(source => source.CreatedAt)
            .ToListAsync(ct);

        // Best take per slot for the sheet's tile preview: kept beats candidate (it's the
        // one a person chose), then newest. Discarded takes never resurface.
        var activeTakes = await db.ImageVariants
                .Where(v => v.CampaignId == id && v.State != "Discarded")
                .Select(v => new { v.SlotId, v.ThumbUrl, v.State, v.CreatedAt })
            .ToListAsync(ct);
        var latestTakeBySlot = activeTakes
            .GroupBy(v => v.SlotId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(v => v.State == "Kept" ? 0 : 1)
                    .ThenByDescending(v => v.CreatedAt)
                    .First().ThumbUrl);

        var brand = campaign.BrandId is { } brandId
            ? await db.BrandProfiles
                .Where(b => b.Id == brandId)
                .Select(b => new BrandSummaryResponse(b.Id, b.Name))
                .SingleOrDefaultAsync(ct)
            : null;

        return Results.Ok(new
        {
            campaign = ToResponse(campaign),
            brand,
            artifacts,
            sources = sources.Select(EvidenceEndpoints.ToSourceResponse).ToList(),
            imageSlots = slots
                .OrderBy(s => Array.FindIndex(Services.Images.ImagePlanService.Templates, t => t.Kind == s.Kind))
                .Select(s => ImageSlotEndpoints.ToResponse(s) with
                {
                    LatestTakeThumbUrl = latestTakeBySlot.GetValueOrDefault(s.Id),
                    ActiveTakeCount = activeTakes.Count(v => v.SlotId == s.Id),
                })
                .ToList(),
            imagesFilled = slots.Count(s => s.State == "Filled"),
            imagesTotal = slots.Count,
        });
    }

    /// <summary>
    /// Cross-campaign dashboard projection: the review queue, aging drafts, per-campaign
    /// counters and the empty-slot summary — grouped server-side so the payload stays a
    /// few KB regardless of how much content the campaigns hold.
    /// </summary>
    private static async Task<IResult> DashboardAsync(
        CastmillDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var campaigns = await db.Campaigns
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new { c.Id, c.Name, c.UpdatedAt })
            .ToListAsync(ct);
        var names = campaigns.ToDictionary(c => c.Id, c => c.Name);

        var artifactCounts = await db.Artifacts
            .GroupBy(a => a.CampaignId)
            .Select(g => new
            {
                CampaignId = g.Key,
                Total = g.Count(),
                Draft = g.Count(a => a.Status == ArtifactStatus.Draft
                    && !DashboardHiddenArtifactKinds.Contains(a.Kind)),
                InReview = g.Count(a => a.Status == ArtifactStatus.InReview
                    && !DashboardHiddenArtifactKinds.Contains(a.Kind)),
                Reviewed = g.Count(a => a.Status == ArtifactStatus.Queued
                    && !DashboardHiddenArtifactKinds.Contains(a.Kind)),
                Published = g.Count(a => a.Status == ArtifactStatus.Published
                    && !DashboardHiddenArtifactKinds.Contains(a.Kind)),
            })
            .ToListAsync(ct);

        var slotCounts = await db.ImageSlots
            .GroupBy(s => s.CampaignId)
            .Select(g => new
            {
                CampaignId = g.Key,
                Total = g.Count(),
                Filled = g.Count(s => s.State == "Filled"),
            })
            .ToListAsync(ct);

        var review = await db.Artifacts
            .Where(a => a.Status == ArtifactStatus.InReview
                && !DashboardHiddenArtifactKinds.Contains(a.Kind))
            .OrderByDescending(a => a.UpdatedAt)
            .Take(12)
            .Select(a => new { a.CampaignId, a.Id, a.Kind, a.Title, a.Status, a.UpdatedAt })
            .ToListAsync(ct);

        var reviewCounts = await db.Artifacts
            .Where(a => !DashboardHiddenArtifactKinds.Contains(a.Kind))
            .GroupBy(_ => 1)
            .Select(group => new ReviewDeskCounts(
                group.Count(a => a.Status == ArtifactStatus.Draft),
                group.Count(a => a.Status == ArtifactStatus.InReview),
                group.Count(a => a.Status == ArtifactStatus.Queued),
                group.Count(a => a.Status == ArtifactStatus.Published)))
            .SingleOrDefaultAsync(ct)
            ?? new ReviewDeskCounts(0, 0, 0, 0);

        var agingCutoff = clock.GetUtcNow() - TimeSpan.FromDays(7);
        var aging = await db.Artifacts
            .Where(a => a.Status == ArtifactStatus.Draft
                && !DashboardHiddenArtifactKinds.Contains(a.Kind)
                && a.UpdatedAt < agingCutoff)
            .OrderBy(a => a.UpdatedAt)
            .Take(50)
            .Select(a => new { a.CampaignId, a.Id, a.Kind, a.Title, a.Status, a.UpdatedAt })
            .ToListAsync(ct);

        // The Wire's queue: reviewed and waiting for a slot (ADR-F22's Queued state).
        var readyToSchedule = await db.Artifacts
            .Where(a => a.Status == ArtifactStatus.Queued
                && ArtifactKinds.DistributionContent.Contains(a.Kind)
                && !db.ScheduleEntries.Any(entry => entry.ArtifactId == a.Id))
            .OrderBy(a => a.UpdatedAt)
            .Take(50)
            .Select(a => new { a.CampaignId, a.Id, a.Kind, a.Title, a.Status, a.UpdatedAt })
            .ToListAsync(ct);

        var emptySlotModels = await db.ImageSlots
            .Where(s => s.State != "Filled" && s.ModelAlias != null)
            .Select(s => s.ModelAlias!)
            .Distinct()
            .ToListAsync(ct);

        // One placed image per campaign for the card's media band. Published blob paths are
        // reused across placements, so the URL is cache-busted with the slot's UpdatedAt —
        // same convention the studio uses.
        var heroSlots = await db.ImageSlots
            .Where(s => s.State == "Filled" && s.PublishedUrl != null)
            .Select(s => new { s.CampaignId, s.PublishedUrl, s.UpdatedAt })
            .ToListAsync(ct);
        var heroByCampaign = heroSlots
            .GroupBy(s => s.CampaignId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(s => s.UpdatedAt)
                    .Select(s => $"{s.PublishedUrl}?v={s.UpdatedAt.ToUnixTimeSeconds()}")
                    .First());

        // Campaigns with generated takes but nothing placed yet still get a band image —
        // the best take (kept first, then newest) beats a blank placeholder for reference.
        var takesByCampaign = (await db.ImageVariants
                .Where(v => v.State != "Discarded")
                .Select(v => new { v.CampaignId, v.ThumbUrl, v.State, v.CreatedAt })
                .ToListAsync(ct))
            .GroupBy(v => v.CampaignId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(v => v.State == "Kept" ? 0 : 1)
                    .ThenByDescending(v => v.CreatedAt)
                    .First());
        foreach (var (campaignId, take) in takesByCampaign)
        {
            if (!heroByCampaign.ContainsKey(campaignId))
            {
                heroByCampaign[campaignId] = $"{take.ThumbUrl}?v={take.CreatedAt.ToUnixTimeSeconds()}";
            }
        }

        var counts = campaigns.Select(c =>
        {
            var a = artifactCounts.FirstOrDefault(x => x.CampaignId == c.Id);
            var s = slotCounts.FirstOrDefault(x => x.CampaignId == c.Id);
            return new CampaignCounts(
                c.Id,
                a?.Total ?? 0,
                a?.InReview ?? 0,
                s?.Filled ?? 0,
                s?.Total ?? 0,
                heroByCampaign.GetValueOrDefault(c.Id),
                a?.Draft ?? 0,
                a?.Reviewed ?? 0,
                a?.Published ?? 0);
        }).ToList();

        var withEmpty = slotCounts.Where(s => s.Total > s.Filled).Select(s => s.CampaignId).ToHashSet();

        return Results.Ok(new DashboardResponse(
            review.Select(a => new DashboardArtifact(
                a.CampaignId, names.GetValueOrDefault(a.CampaignId, ""), a.Id,
                a.Kind, a.Title, a.Status, a.UpdatedAt)).ToList(),
            aging.Select(a => new DashboardArtifact(
                a.CampaignId, names.GetValueOrDefault(a.CampaignId, ""), a.Id,
                a.Kind, a.Title, a.Status, a.UpdatedAt)).ToList(),
            counts,
            slotCounts.Sum(s => s.Total - s.Filled),
            withEmpty.Count,
            emptySlotModels,
            campaigns.FirstOrDefault(c => withEmpty.Contains(c.Id))?.Id,
            readyToSchedule.Select(a => new DashboardArtifact(
                a.CampaignId, names.GetValueOrDefault(a.CampaignId, ""), a.Id,
                a.Kind, a.Title, a.Status, a.UpdatedAt)).ToList(),
            reviewCounts));
    }

    private static async Task<IResult> ReviewDeskAsync(
        string status,
        int? skip,
        int? take,
        CastmillDbContext db,
        CancellationToken ct)
    {
        if (!ArtifactStatus.IsValid(status))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["status"] = [$"Status must be one of: {string.Join(", ", ArtifactStatus.All)}."],
            });
        }

        var offset = Math.Max(0, skip ?? 0);
        var pageSize = Math.Clamp(take ?? 12, 1, 50);
        var query = db.Artifacts.Where(a =>
            a.Status == status && !DashboardHiddenArtifactKinds.Contains(a.Kind));
        var total = await query.CountAsync(ct);
        var rows = await (
            from artifact in query
            join campaign in db.Campaigns on artifact.CampaignId equals campaign.Id
            orderby artifact.UpdatedAt descending
            select new
            {
                artifact.CampaignId,
                CampaignName = campaign.Name,
                ArtifactId = artifact.Id,
                artifact.Kind,
                artifact.Title,
                artifact.Status,
                artifact.UpdatedAt,
            })
            .Skip(offset)
            .Take(pageSize)
            .ToListAsync(ct);

        return Results.Ok(new ReviewDeskResponse(
            status,
            total,
            rows.Select(item => new DashboardArtifact(
                item.CampaignId,
                item.CampaignName,
                item.ArtifactId,
                item.Kind,
                item.Title,
                item.Status,
                item.UpdatedAt)).ToList()));
    }

    private static async Task<IResult> CreateAsync(
        CampaignCreateRequest request,
        ClaimsPrincipal principal,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await ValidateBrandAndLinksAsync(
                request.BrandId, request.Links, db, ct, contentType: request.ContentType) is { } invalid)
        {
            return invalid;
        }
        if (ValidateRunPlan(request.Intent, request.OutputRecipe) is { } invalidPlan)
        {
            return invalidPlan;
        }

        var now = clock.GetUtcNow();
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId!.Value,
            OwnerId = AuthEndpoints.GetUserId(principal),
            Name = request.Name,
            Brief = request.Brief,
            BrandId = request.BrandId,
            ContentType = request.ContentType,
            Intent = request.Intent,
            SkipSeoAnalysis = request.SkipSeoAnalysis,
            OutputRecipeJson = request.OutputRecipe is null
                ? null
                : JsonSerializer.Serialize(request.OutputRecipe, Json),
            ContextJson = request.Links is null
                ? null
                : System.Text.Json.JsonSerializer.Serialize(request.Links, Json),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/campaigns/{campaign.Id}", ToResponse(campaign));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        CampaignUpdateRequest request,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
        {
            return Results.NotFound();
        }

        if (await ValidateBrandAndLinksAsync(
                request.BrandId, request.Links, db, ct, request.Status, request.ContentType) is { } invalid)
        {
            return invalid;
        }
        var nextIntent = request.Intent ?? campaign.Intent;
        var nextRecipe = request.OutputRecipe ?? ParseOutputRecipe(campaign.OutputRecipeJson);
        if (ValidateRunPlan(nextIntent, nextRecipe) is { } invalidPlan)
        {
            return invalidPlan;
        }

        var inputsChanged = !string.Equals(campaign.Brief, request.Brief, StringComparison.Ordinal)
            || campaign.BrandId != request.BrandId
            || !string.Equals(campaign.ContentType, request.ContentType, StringComparison.Ordinal)
            || !string.Equals(campaign.Intent, nextIntent, StringComparison.Ordinal)
            || campaign.SkipSeoAnalysis != request.SkipSeoAnalysis;
        campaign.Name = request.Name;
        campaign.Brief = request.Brief;
        campaign.BrandId = request.BrandId;
        campaign.Status = request.Status;
        campaign.ContentType = request.ContentType;
        campaign.Intent = nextIntent;
        campaign.SkipSeoAnalysis = request.SkipSeoAnalysis;
        campaign.OutputRecipeJson = nextRecipe.Count == 0
            ? null
            : JsonSerializer.Serialize(nextRecipe, Json);
        campaign.ContextJson = request.Links is null
            ? null
            : System.Text.Json.JsonSerializer.Serialize(request.Links, Json);
        campaign.UpdatedAt = clock.GetUtcNow();
        if (inputsChanged)
        {
            await MarkLatestReportStaleAsync(
                campaign.Id, db, campaign.UpdatedAt, inputs: true, ct: ct);
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(campaign));
    }

    internal static async Task MarkLatestReportStaleAsync(
        Guid campaignId, CastmillDbContext db, DateTimeOffset now,
        bool inputs = false, bool angles = false, bool share = false,
        CancellationToken ct = default)
    {
        var artifact = await db.Artifacts
            .Where(a => a.CampaignId == campaignId && a.Kind == "seo-report")
            .OrderByDescending(a => a.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        if (artifact is null)
        {
            return;
        }

        try
        {
            var report = JsonSerializer.Deserialize<SeoAnalysisReportResponse>(artifact.ContentJson, TargetsJson);
            if (report is null)
            {
                return;
            }
            var nextInputsStale = report.InputsStale || inputs;
            var nextAnglesStale = report.AnglesStale || angles;
            var nextShareStale = report.ShareStale || share || report.SharedAt is not null;
            if (nextInputsStale == report.InputsStale
                && nextAnglesStale == report.AnglesStale
                && nextShareStale == report.ShareStale)
            {
                return;
            }
            artifact.ContentJson = JsonSerializer.Serialize(report with
            {
                InputsStale = nextInputsStale,
                AnglesStale = nextAnglesStale,
                ShareStale = nextShareStale,
            }, TargetsJson);
            artifact.Version++;
            artifact.UpdatedAt = now;
        }
        catch (JsonException)
        {
        }
    }

    private static async Task<IResult> DeleteAsync(Guid id, CastmillDbContext db, CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
        {
            return Results.NotFound();
        }

        // Children have no FK cascade (typed-JSON rows, ADR-003) — delete explicitly.
        await db.ArtifactRevisions
            .Where(r => db.Artifacts.Any(a => a.Id == r.ArtifactId && a.CampaignId == id))
            .ExecuteDeleteAsync(ct);
        await db.Artifacts.Where(a => a.CampaignId == id).ExecuteDeleteAsync(ct);
        await db.ImageVariants.Where(v => v.CampaignId == id).ExecuteDeleteAsync(ct);
        await db.ImageSlots.Where(s => s.CampaignId == id).ExecuteDeleteAsync(ct);
        await db.ScheduleEntries.Where(s => s.CampaignId == id).ExecuteDeleteAsync(ct);
        await db.GenerationRuns.Where(r => r.CampaignId == id).ExecuteDeleteAsync(ct);
        db.Campaigns.Remove(campaign);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
