using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Api.Services.Evidence;
using Castmill.Api.Services.Knowledge;
using Castmill.Api.Services.Scout;
using Castmill.Api.Services.Secrets;
using Castmill.Api.Services.Seo;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Endpoints;

public sealed record TranscribeRequest(
    [property: Required] Guid AssetId,
    /// <summary>Force the Azure Speech long-media path regardless of size.</summary>
    bool UseSpeech = false);

public static class AiEndpoints
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed record MediaTimeRangeLocator(
        double StartSeconds,
        double EndSeconds,
        string? Speaker,
        string SourceLabel);

    private sealed record TextSegmentLocator(int Position, string? Speaker, string SourceLabel);

    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/ai").RequireAuthorization("TenantAllowed");

        group.MapGet("/status", StatusAsync);
        group.MapGet("/log", Log);
        group.MapPost("/campaigns/{campaignId:guid}/transcripts", IngestTranscriptAsync)
            .Validate<TranscriptIngestRequest>().RequireRateLimiting("writes");
        group.MapPost("/campaigns/{campaignId:guid}/transcribe", TranscribeAsync)
            .Validate<TranscribeRequest>().RequireRateLimiting("ai");
        group.MapPost("/campaigns/{campaignId:guid}/generate", GenerateAsync)
            .Validate<GenerateRequest>().RequireRateLimiting("ai");
        group.MapPost("/campaigns/{campaignId:guid}/generate/{kind}", GenerateOneAsync)
            .Validate<GenerateRequest>().RequireRateLimiting("ai");
        group.MapPost("/campaigns/{campaignId:guid}/artifacts/{artifactId:guid}/youtube-titles/{slot}/regenerate",
                RegenerateYoutubeTitleAsync)
            .RequireRateLimiting("ai");
        // Second pass over one existing artifact (ADR-020). Revises in place behind a
        // revision snapshot, so unlike generate it returns the artifact, not a new row.
        group.MapPost("/campaigns/{campaignId:guid}/artifacts/{artifactId:guid}/tech-edit", TechEditAsync)
            .Validate<TechEditRequest>().RequireRateLimiting("ai");
        // The Content Scout (E4): an agent loop, so it is on the "ai" partition.
        group.MapPost("/campaigns/{campaignId:guid}/brief", SuggestBriefAsync)
            .RequireRateLimiting("ai");
        group.MapPost("/campaigns/{campaignId:guid}/research-context", SuggestResearchContextAsync)
            .RequireRateLimiting("ai");

        group.MapPost("/campaigns/{campaignId:guid}/scout", ScoutAsync)
            .Validate<ScoutRequest>().RequireRateLimiting("ai");
        // B9.8: Press Run progress. A plain read, so it is not on the "ai" partition —
        // polling progress must never consume the generation budget.
        group.MapGet("/runs/{runId:guid}", RunAsync);
        group.MapGet("/campaigns/{campaignId:guid}/runs/latest", LatestRunAsync);
        return routes;
    }

    /// <summary>Per-artifact completions for the Press Run reveal (B9.8).</summary>
    /// <summary>
    /// The campaign's most recent run. Exists because the generate POST is a buffered
    /// response: its run id only reaches the client when generation has already finished,
    /// which is exactly too late for the Press Run to poll. The client starts the POST,
    /// navigates, and discovers the in-flight run here.
    /// </summary>
    private static async Task<IResult> LatestRunAsync(
        Guid campaignId, string? kind, CastmillDbContext db, CancellationToken ct)
    {
        // Defaults to content runs so the Press Run never adopts an image run; the
        // Image Studio polls with ?kind=image (or by the run id it was handed).
        var wanted = string.IsNullOrWhiteSpace(kind) ? "content" : kind;
        var run = await db.GenerationRuns
            .Where(r => r.CampaignId == campaignId && r.Kind == wanted)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);
        return run is null ? Results.NotFound() : ProjectRun(run);
    }

    /// <summary>Client segments are trusted for times/speakers but never for ids: ids are
    /// reassigned to the canonical s01… form every other surface expects.</summary>
    private static TranscriptContent NormalizeSegments(IReadOnlyList<TranscriptSegment> segments, string? source)
    {
        var normalized = segments
            .Where(s => !string.IsNullOrWhiteSpace(s.Text))
            .OrderBy(s => s.StartSeconds)
            .Select((s, i) => new TranscriptSegment(
                $"s{i + 1:00}",
                Math.Max(0, s.StartSeconds),
                Math.Max(s.StartSeconds, s.EndSeconds),
                string.IsNullOrWhiteSpace(s.Speaker) ? null : s.Speaker,
                s.Text.Trim(),
                string.IsNullOrWhiteSpace(s.SourceLabel) ? source : s.SourceLabel.Trim()))
            .ToList();
        return new TranscriptContent(source ?? "local-transcription", normalized);
    }

    private static async Task<IResult> RunAsync(Guid runId, CastmillDbContext db, CancellationToken ct)
    {
        var run = await db.GenerationRuns.SingleOrDefaultAsync(r => r.Id == runId, ct);
        return run is null ? Results.NotFound() : ProjectRun(run);
    }

    private static IResult ProjectRun(GenerationRun run)
    {
        using var items = JsonDocument.Parse(run.ItemsJson);
        var completed = items.RootElement.EnumerateArray().Count(item =>
            !item.TryGetProperty("outcome", out var outcome)
            || !string.Equals(outcome.GetString(), "Generating", StringComparison.OrdinalIgnoreCase));
        return Results.Ok(new
        {
            run.Id,
            run.CampaignId,
            run.Status,
            run.TotalKinds,
            completed,
            items = items.RootElement.Clone(),
            run.StartedAt,
            run.UpdatedAt,
        });
    }

    // ---- Status & transparency ----------------------------------------------

    private static async Task<IResult> StatusAsync(
        bool? probe,
        ClaimsPrincipal principal,
        IFoundryClientFactory clients,
        IImageProviderRegistry imageProviders,
        IChatProviderRegistry textProviders,
        IKnowledgeBaseClient knowledge,
        IUserSecretsService secrets,
        IOptions<AiOptions> options,
        CancellationToken ct)
    {
        var userId = AuthEndpoints.GetUserId(principal);
        var credentials = await clients.ResolveCredentialsAsync(userId, ct);
        var models = options.Value.Models
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        string? probeResult = null;
        if (probe == true && credentials is not null && models.ContainsKey("chat"))
        {
            try
            {
                var client = await clients.CreateChatClientAsync(userId, "chat", ct);
                var response = await client.GetResponseAsync("Reply with the single word: ok", cancellationToken: ct);
                probeResult = string.IsNullOrWhiteSpace(response.Text) ? "empty response" : "ok";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Actionable, but never includes the endpoint/key themselves.
                probeResult = $"probe failed: {ex.GetType().Name}";
            }
        }

        // Per-provider readiness (B9.5): the client disables a model with a reason
        // instead of letting the user watch a generate fail.
        var providers = await imageProviders.StatusAsync(userId, ct);

        // Same idea for text (ADR-020): the Tech Edit button reads this so an unconfigured
        // provider is a disabled control with a reason, never a failed click (G3).
        var text = await textProviders.StatusAsync(userId, ct);
        // Config plus a stored token. Deliberately not a live probe: /status is polled by
        // every client on load, and a round trip to someone else's gateway on each one is a
        // cost we would be imposing on them, not ourselves.
        var knowledgeReady = knowledge.IsConfigured
            && await secrets.GetAsync(userId, SecretKind.KnowledgeBaseToken, ct) is { Length: > 0 };

        return Results.Ok(new AiStatusResponse(
            credentials?.Source ?? "none",
            credentials is not null,
            models,
            options.Value.Speech.IsConfigured,
            probeResult,
            [.. providers.Select(p => new ImageProviderReadiness(
                p.Name, p.Ready, p.Reason, p.SupportsReferenceImages))],
            [.. text.Select(p => new TextProviderReadiness(p.Name, p.Ready, p.Reason))],
            knowledgeReady));
    }

    private static IResult Log(ClaimsPrincipal principal, IPromptLog promptLog) =>
        Results.Ok(promptLog.ForUser(AuthEndpoints.GetUserId(principal)));

    // ---- Transcript ingest ----------------------------------------------------

    private static async Task<IResult> IngestTranscriptAsync(
        Guid campaignId,
        TranscriptIngestRequest request,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(c => c.Id == campaignId, ct))
        {
            return Results.NotFound();
        }
        // Client-supplied timed segments (desktop local transcription) win over
        // sentence-splitting: they carry the real timestamps.
        var transcript = request.Segments is { Count: > 0 }
            ? NormalizeSegments(request.Segments, request.Source)
            : TranscriptService.FromPlainText(request.Text, request.Source);
        if (transcript.Segments.Count == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Text"] = ["No usable sentences found in the pasted text."],
            });
        }
        var modality = request.Segments is { Count: > 0 }
            ? SourceModalities.Media
            : SourceModalities.Text;
        var persisted = await PersistTranscriptAsync(
            campaignId, transcript, modality, null, tenant, db, clock, ct);
        return Results.Created(
            $"/api/v1/campaigns/{campaignId}/artifacts/{persisted.Id}",
            new { transcriptArtifactId = persisted.Id, segmentCount = transcript.Segments.Count });
    }

    private static async Task<IResult> TranscribeAsync(
        Guid campaignId,
        TranscribeRequest request,
        ClaimsPrincipal principal,
        ITranscriptionService transcription,
        IBlobSasService blobs,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(c => c.Id == campaignId, ct))
        {
            return Results.NotFound();
        }
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.Id == request.AssetId, ct);
        if (asset is null)
        {
            return Results.NotFound();
        }

        var blob = await blobs.OpenReadAsync(asset.BlobPath, ct);
        if (blob is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                detail: "Asset is registered but its file has not been uploaded yet.");
        }

        await using var stream = blob.Value.Stream;
        TranscriptContent transcript;
        try
        {
            if (request.UseSpeech || blob.Value.Length > TranscriptionService.ShortPathMaxBytes)
            {
                transcript = await transcription.TranscribeLongAsync(stream, asset.FileName, ct);
            }
            else
            {
                transcript = await transcription.TranscribeShortAsync(
                    AuthEndpoints.GetUserId(principal), stream, asset.FileName, ct);
            }
        }
        catch (AiNotConfiguredException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
        }

        var persisted = await PersistTranscriptAsync(
            campaignId, transcript, SourceModalities.Media, asset, tenant, db, clock, ct);
        return Results.Created(
            $"/api/v1/campaigns/{campaignId}/artifacts/{persisted.Id}",
            new { transcriptArtifactId = persisted.Id, segmentCount = transcript.Segments.Count });
    }

    // ---- Generation -----------------------------------------------------------

    private static async Task<IResult> GenerateAsync(
        Guid campaignId,
        GenerateRequest request,
        ClaimsPrincipal principal,
        IAiOrchestrator orchestrator,
        IContentDependencyService dependencies,
        CastmillDbContext db,
        IOptions<SeoOptions> seoOptions,
        HttpResponse response,
        CancellationToken ct)
    {
        var loaded = await LoadForGenerationAsync(
            campaignId, request.TranscriptArtifactId, dependencies, db, ct);
        if (loaded is not var (campaign, transcript))
        {
            return Results.NotFound();
        }

        if (seoOptions.Value.RequireAnalysisBeforeGeneration && !campaign.SkipSeoAnalysis
            && !await HasApprovedSeoAnalysisAsync(campaign, db, ct))
        {
            return SeoAnalysisRequired();
        }

        var unsupportedKinds = (request.Kinds ?? [])
            .Select(Generators.Normalize)
            .Where(kind => !ArtifactKinds.IsUserContent(kind)
                && kind is not ("image-prompts" or "thumbnail-concepts"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unsupportedKinds.Count > 0)
        {
            return Results.Problem(
                $"These are not Mill content types: {string.Join(", ", unsupportedKinds)}.",
                statusCode: 400);
        }

        // Open the run row first: the client can start polling /ai/runs/{id} the
        // moment it has the id, while this request is still generating (B9.8).
        var runId = await orchestrator.StartRunAsync(campaignId, request.Kinds, ct, request.Count);
        response.Headers.Append("Castmill-Run-Id", runId.ToString());

        // The fan-out is deliberately NOT cancelled when the request is. A run used to live
        // and die with this HTTP call, so a client timeout, a closed laptop, a navigation or
        // a dropped connection silently truncated it part-way — "13 items promised, fewer
        // made", with the paid model calls already spent. The client recovers by polling the
        // run row; the only things that stop a run now are the app shutting down and the
        // 30-minute cap, which exists so an orphaned run cannot burn model spend forever.
        using var runScope = CancellationTokenSource.CreateLinkedTokenSource(
            response.HttpContext.RequestServices
                .GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>()
                .ApplicationStopping);
        runScope.CancelAfter(TimeSpan.FromMinutes(30));

        var results = await orchestrator.RunFanOutAsync(
            AuthEndpoints.GetUserId(principal), campaign, transcript, request.Brief, request.Kinds,
            runScope.Token, runId, request.Count);

        // Per-phase costs for the Press Run UI (G7).
        response.Headers.Append("Server-Timing",
            string.Join(", ", results.Select(r => $"{r.Kind};dur={r.DurationMs}")));
        return Results.Ok(new
        {
            runId,
            succeeded = results.Count(r => r.Success),
            failed = results.Count(r => !r.Success),
            results,
        });
    }

    private static async Task<IResult> GenerateOneAsync(
        Guid campaignId,
        string kind,
        GenerateRequest request,
        ClaimsPrincipal principal,
        IAiOrchestrator orchestrator,
        IContentDependencyService dependencies,
        CastmillDbContext db,
        IOptions<SeoOptions> seoOptions,
        CancellationToken ct)
    {
        var loaded = await LoadForGenerationAsync(
            campaignId, request.TranscriptArtifactId, dependencies, db, ct);
        if (loaded is not var (campaign, transcript))
        {
            return Results.NotFound();
        }
        if (seoOptions.Value.RequireAnalysisBeforeGeneration && !campaign.SkipSeoAnalysis
            && !await HasApprovedSeoAnalysisAsync(campaign, db, ct))
        {
            return SeoAnalysisRequired();
        }
        var userId = AuthEndpoints.GetUserId(principal);

        if (!ArtifactKinds.IsUserContent(Generators.Normalize(kind)))
        {
            return Results.Problem($"{kind} is not a Mill content type.", statusCode: 400);
        }

        if (request.ParentArtifactId is { } parentId
            && !await db.Artifacts.AnyAsync(
                a => a.Id == parentId && a.CampaignId == campaignId && a.Kind == "blog", ct))
        {
            return Results.Problem("A generated child must belong to a blog in this campaign.", statusCode: 400);
        }
        if (request.ReplaceArtifactId is { } replaceId
            && !await db.Artifacts.AnyAsync(
                a => a.Id == replaceId && a.CampaignId == campaignId
                    && a.Kind == Generators.Normalize(kind), ct))
        {
            return Results.Problem("The placeholder does not match this campaign and content kind.", statusCode: 400);
        }

        if (kind.Equals("blog", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(await orchestrator.RunBlogAsync(
                userId, campaign, transcript, request.Brief, ct, request.ReplaceArtifactId));
        }
        var spec = Generators.Find(kind);
        if (spec is null)
        {
            return Results.NotFound();
        }
        return Results.Ok(await orchestrator.RunGeneratorAsync(
            userId, campaign, transcript, request.Brief, spec, ct,
            request.ParentArtifactId, request.ReplaceArtifactId));
    }

    private static async Task<bool> HasApprovedSeoAnalysisAsync(
        Campaign campaign, CastmillDbContext db, CancellationToken ct)
    {
        var targets = CampaignEndpoints.ParseSeoTargets(campaign.SeoTargetsJson);
        return targets.Keywords.Count > 0
            && await db.Artifacts.AnyAsync(
                a => a.CampaignId == campaign.Id && a.Kind == "seo-report", ct);
    }

    private static async Task<IResult> RegenerateYoutubeTitleAsync(
        Guid campaignId,
        Guid artifactId,
        string slot,
        YoutubeTitleRegenerationRequest request,
        ClaimsPrincipal principal,
        IAiOrchestrator orchestrator,
        IContentDependencyService dependencies,
        CastmillDbContext db,
        CancellationToken ct)
    {
        slot = slot.ToUpperInvariant();
        if (slot is not ("A" or "B" or "C"))
        {
            return Results.Problem("Title slot must be A, B, or C.", statusCode: 400);
        }
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId, ct);
        var artifact = await db.Artifacts.SingleOrDefaultAsync(
            a => a.Id == artifactId && a.CampaignId == campaignId && a.Kind == "youtube", ct);
        var transcriptArtifactId = await db.Artifacts
            .Where(a => a.CampaignId == campaignId && a.Kind == "transcript")
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        var loaded = await LoadForGenerationAsync(
            campaignId, transcriptArtifactId, dependencies, db, ct);
        if (campaign is null || artifact is null || loaded is null)
        {
            return Results.NotFound();
        }
        try
        {
            return Results.Ok(await orchestrator.RegenerateYoutubeTitleAsync(
                AuthEndpoints.GetUserId(principal), campaign, artifact, loaded.Value.Transcript,
                slot, request.Steering, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: 409);
        }
    }

    private static IResult SeoAnalysisRequired() => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "SEO/AEO analysis required",
        detail: "Run the campaign's SEO/AEO analysis and approve at least one target before generating content.");

    /// <summary>
    /// Runs the Tech Edit over one artifact (ADR-020). Follows the AI group's convention of
    /// reporting a misconfigured provider as an unsuccessful result rather than a 500 — the
    /// user pressed a button, and a 500 tells them nothing they can act on.
    /// </summary>
    private static async Task<IResult> TechEditAsync(
        Guid campaignId,
        Guid artifactId,
        TechEditRequest request,
        ClaimsPrincipal principal,
        IAiOrchestrator orchestrator,
        IContentDependencyService dependencies,
        CastmillDbContext db,
        HttpResponse response,
        CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId, ct);
        if (campaign is null)
        {
            return Results.NotFound();
        }

        var artifact = await db.Artifacts.SingleOrDefaultAsync(
            a => a.Id == artifactId && a.CampaignId == campaignId, ct);
        if (artifact is null)
        {
            return Results.NotFound();
        }

        if (artifact.Kind.Equals("transcript", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "A transcript is source material, not an artifact to edit.");
        }

        var transcriptArtifactId = await db.Artifacts
            .Where(a => a.CampaignId == campaignId && a.Kind == "transcript")
            .OrderBy(a => a.CreatedAt)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        var loaded = await LoadForGenerationAsync(
            campaignId, transcriptArtifactId, dependencies, db, ct);
        if (loaded is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "This campaign has no approved evidence to check the edit against.");
        }

        var result = await orchestrator.RunTechEditAsync(
            AuthEndpoints.GetUserId(principal), campaign, artifact, loaded.Value.Transcript,
            request.Steering, request.UseKnowledgeBase, ct);

        if (result.Success)
        {
            response.Headers.ETag = $"\"{result.Version}\"";
        }
        return Results.Ok(result);
    }

    /// <summary>
    /// Proposes what to make next, gap-checked against what is already published and already
    /// drafted. Reports a misconfigured provider as an unsuccessful result rather than a 500,
    /// like every other generation path here.
    /// </summary>
    private static async Task<IResult> ScoutAsync(
        Guid campaignId,
        ScoutRequest request,
        ClaimsPrincipal principal,
        IContentScout scout,
        CastmillDbContext db,
        CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId, ct);
        if (campaign is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(await scout.RunAsync(
            AuthEndpoints.GetUserId(principal), campaign, request.Focus, request.Count, ct));
    }

    // ---- Shared ----------------------------------------------------------------

    /// <summary>
    /// Reads the brief off the transcript. Reports failure as a RESULT rather than a 500, the
    /// same contract as every other generation call here — a brief that could not be drafted
    /// must leave the user typing it themselves, not staring at an error page.
    /// </summary>
    private static async Task<IResult> SuggestBriefAsync(
        Guid campaignId,
        Guid? transcriptArtifactId,
        string? title,
        ClaimsPrincipal principal,
        IBriefSuggester suggester,
        IBrandContextService brandContext,
        IContentDependencyService dependencies,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        IOptions<SeoOptions> seoOptions,
        CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(
            candidate => candidate.Id == campaignId, ct);
        var source = await dependencies.LoadApprovedSourceAsync(
            campaignId, transcriptArtifactId, ct);
        if (campaign is null || source is null)
        {
            return Results.NotFound();
        }
        if (seoOptions.Value.RequireAnalysisBeforeGeneration && !campaign.SkipSeoAnalysis
            && !await HasApprovedSeoAnalysisAsync(campaign, db, ct))
        {
            return SeoAnalysisRequired();
        }

        try
        {
            var context = await brandContext.ResolveAsync(campaign, ct);
            var strategy = string.Join("\n\n", new[]
            {
                context.SeoTargetBlock,
                string.IsNullOrWhiteSpace(campaign.ContentType)
                    ? null
                    : $"Required content format: {campaign.ContentType}.",
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var brief = await suggester.SuggestAsync(
                AuthEndpoints.GetUserId(principal), source, title,
                strategy, ct);

            await PersistSummaryAsync(
                campaign, brief, dependencies, tenant, db, clock.GetUtcNow(), ct);

            return Results.Ok(new BriefSuggestionResponse(
                brief.Title, brief.Audience, brief.BrandVoice, brief.Angle,
                brief.Summary, brief.KeyPoints));
        }
        catch (AiNotConfiguredException ex)
        {
            return Results.Problem(ex.Message, statusCode: 409);
        }
        catch (JsonException)
        {
            return Results.Problem("The model's brief could not be read.", statusCode: 502);
        }
    }

    private static async Task PersistSummaryAsync(
        Campaign campaign, BriefSuggestion brief, IContentDependencyService dependencies,
        ITenantProvider tenant,
        CastmillDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(brief.Summary) && brief.KeyPoints.Count == 0)
        {
            return;
        }

        var targets = CampaignEndpoints.ParseSeoTargets(campaign.SeoTargetsJson);
        var markdown = new System.Text.StringBuilder();
        markdown.AppendLine("# Executive summary").AppendLine();
        markdown.AppendLine(brief.Summary ?? "Summary pending review.").AppendLine();
        markdown.AppendLine("## Key takeaways").AppendLine();
        foreach (var point in brief.KeyPoints)
        {
            markdown.Append("1. ").AppendLine(point);
        }
        markdown.AppendLine().AppendLine("## Keyword opportunities").AppendLine();
        foreach (var keyword in targets.Keywords.Take(10))
        {
            markdown.Append("- ").Append(keyword.Term);
            if (keyword.Volume is { } volume)
            {
                markdown.Append(" — ").Append(volume.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" monthly searches");
            }
            markdown.AppendLine();
        }

        var content = JsonSerializer.Serialize(new
        {
            markdown = markdown.ToString().Trim(),
            summary = brief.Summary,
            keyPoints = brief.KeyPoints,
            keywordOpportunities = targets.Keywords.Take(10).Select(keyword => keyword.Term).ToList(),
        }, WebJson);
        var existing = await db.Artifacts.SingleOrDefaultAsync(
            artifact => artifact.CampaignId == campaign.Id && artifact.Kind == "campaign-summary", ct);
        Artifact summary;
        if (existing is null)
        {
            summary = new Artifact
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId!.Value,
                CampaignId = campaign.Id,
                Kind = "campaign-summary",
                Title = $"Summary — {campaign.Name}",
                ContentJson = content,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Artifacts.Add(summary);
        }
        else
        {
            summary = existing;
            await ArtifactEndpoints.SnapshotRevisionAsync(db, existing, "ai-generation", now, ct);
            existing.ContentJson = content;
            existing.Version++;
            existing.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await dependencies.CaptureGeneratedAsync(
            summary,
            campaign,
            existing is null ? ContentDependencyReasons.Generated : ContentDependencyReasons.Regenerated,
            ct);
    }

    /// <summary>
    /// Infers the audience that shapes the initial SEO/AEO investigation. Unlike the content
    /// brief endpoint, this is deliberately available before report approval and produces no
    /// title, angle or publishable copy.
    /// </summary>
    private static async Task<IResult> SuggestResearchContextAsync(
        Guid campaignId,
        Guid? transcriptArtifactId,
        ClaimsPrincipal principal,
        IResearchContextSuggester suggester,
        IContentDependencyService dependencies,
        CastmillDbContext db,
        CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(candidate => candidate.Id == campaignId, ct))
        {
            return Results.NotFound();
        }
        var source = await dependencies.LoadApprovedSourceAsync(
            campaignId, transcriptArtifactId, ct);
        if (source is null)
        {
            return Results.NotFound();
        }

        try
        {
            return Results.Ok(await suggester.SuggestAsync(
                AuthEndpoints.GetUserId(principal), source, ct));
        }
        catch (AiNotConfiguredException ex)
        {
            return Results.Problem(ex.Message, statusCode: 409);
        }
        catch (JsonException)
        {
            return Results.Problem("The model's research audience could not be read.", statusCode: 502);
        }
    }

    private static async Task<(Campaign Campaign, TranscriptContent Transcript)?> LoadAsync(
        Guid campaignId,
        Guid transcriptArtifactId,
        IContentDependencyService dependencies,
        CastmillDbContext db,
        CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId, ct);
        if (campaign is null)
        {
            return null;
        }
        if (!await db.Artifacts.AnyAsync(
            artifact => artifact.Id == transcriptArtifactId
                && artifact.CampaignId == campaignId
                && artifact.Kind == "transcript", ct))
        {
            return null;
        }
        var transcript = await dependencies.LoadApprovedTranscriptAsync(
            campaignId, transcriptArtifactId, ct);
        return transcript is null ? null : (campaign, transcript);
    }

    private static async Task<(Campaign Campaign, TranscriptContent Transcript)?> LoadForGenerationAsync(
        Guid campaignId,
        Guid? transcriptArtifactId,
        IContentDependencyService dependencies,
        CastmillDbContext db,
        CancellationToken ct)
    {
        if (transcriptArtifactId is { } artifactId)
        {
            return await LoadAsync(campaignId, artifactId, dependencies, db, ct);
        }

        var campaign = await db.Campaigns.SingleOrDefaultAsync(
            candidate => candidate.Id == campaignId, ct);
        if (campaign is null
            || !await db.SourceAssets.AnyAsync(source =>
                source.CampaignId == campaignId
                && source.ApprovedEvidenceRevision != null, ct))
        {
            return null;
        }
        return (campaign, new TranscriptContent("approved evidence", []));
    }

    internal static async Task<Artifact> PersistTranscriptAsync(
        Guid campaignId, TranscriptContent transcript, string modality, Asset? sourceMedia,
        ITenantProvider tenant,
        CastmillDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var tenantId = tenant.TenantId!.Value;
        var transcriptJson = JsonSerializer.Serialize(transcript, TranscriptService.Json);
        var snapshotHash = EvidenceRevisionHasher.HashContent(transcriptJson);
        var existingSource = await db.SourceAssets
            .Where(source => source.CampaignId == campaignId
                && source.Kind == SourceKinds.Transcript
                && source.SnapshotHash == snapshotHash)
            .OrderBy(source => source.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (existingSource?.LegacyArtifactId is { } existingArtifactId)
        {
            var existingArtifact = await db.Artifacts.SingleOrDefaultAsync(
                artifact => artifact.Id == existingArtifactId
                    && artifact.CampaignId == campaignId
                    && artifact.Kind == "transcript",
                ct);
            if (existingArtifact is not null)
            {
                return existingArtifact;
            }
        }

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CampaignId = campaignId,
            Kind = "transcript",
            Title = $"Transcript — {transcript.Source}",
            ContentJson = transcriptJson,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Artifacts.Add(artifact);

        if (existingSource is not null)
        {
            existingSource.LegacyArtifactId = artifact.Id;
            existingSource.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return artifact;
        }

        var revisionId = Guid.NewGuid();
        var blocks = transcript.Segments
            .Select((segment, ordinal) =>
            {
                var sourceLabel = string.IsNullOrWhiteSpace(segment.SourceLabel)
                    ? transcript.Source
                    : segment.SourceLabel;
                var locatorKind = modality == SourceModalities.Media
                    ? EvidenceLocatorKinds.MediaTimeRange
                    : EvidenceLocatorKinds.TextSegment;
                var locatorJson = modality == SourceModalities.Media
                    ? JsonSerializer.Serialize(new MediaTimeRangeLocator(
                        segment.StartSeconds,
                        segment.EndSeconds,
                        segment.Speaker,
                        sourceLabel),
                        WebJson)
                    : JsonSerializer.Serialize(new TextSegmentLocator(
                        ordinal,
                        segment.Speaker,
                        sourceLabel),
                        WebJson);

                return new EvidenceBlock
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    CampaignId = campaignId,
                    StableId = segment.Id,
                    Ordinal = ordinal,
                    Content = segment.Text,
                    ContentHash = EvidenceRevisionHasher.HashContent(segment.Text),
                    LocatorKind = locatorKind,
                    LocatorJson = locatorJson,
                    Revision = 1,
                    RevisionId = revisionId,
                    ApprovalState = EvidenceApprovalStates.Approved,
                    IsExcluded = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
            })
            .ToList();
        var source = new SourceAsset
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CampaignId = campaignId,
            LegacyArtifactId = artifact.Id,
            Kind = SourceKinds.Transcript,
            Modality = modality,
            Label = transcript.Source,
            BlobPath = sourceMedia?.BlobPath,
            ContentType = sourceMedia?.ContentType,
            SizeBytes = sourceMedia?.SizeBytes,
            SnapshotIdentity = $"sha256:{snapshotHash}",
            SnapshotHash = snapshotHash,
            CurrentEvidenceRevision = 1,
            CurrentEvidenceRevisionId = revisionId,
            ApprovedEvidenceRevision = 1,
            ApprovedEvidenceRevisionId = revisionId,
            ApprovedEvidenceHash = EvidenceRevisionHasher.HashApproved(blocks),
            ApprovedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        foreach (var block in blocks)
        {
            block.SourceAssetId = source.Id;
        }
        db.SourceAssets.Add(source);
        db.EvidenceBlocks.AddRange(blocks);
        await CampaignEndpoints.MarkLatestReportStaleAsync(
            campaignId, db, now, inputs: true, ct: ct);
        await db.SaveChangesAsync(ct);
        return artifact;
    }
}
