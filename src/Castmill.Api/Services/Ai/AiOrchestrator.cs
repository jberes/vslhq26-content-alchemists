using System.Diagnostics;
using System.Text.Json;
using Castmill.Api.Data;
using Castmill.Api.Services.Images;
using Castmill.Api.Services.Knowledge;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Ai;
using Microsoft.Extensions.AI;

namespace Castmill.Api.Services.Ai;

public interface IAiOrchestrator
{
    Task<GenerationResult> RunBlogAsync(Guid userId, Campaign campaign, TranscriptContent transcript, string? brief, CancellationToken ct);
    Task<GenerationResult> RunGeneratorAsync(Guid userId, Campaign campaign, TranscriptContent transcript, string? brief, GeneratorSpec spec, CancellationToken ct);
    /// <summary>
    /// Fan-out. When <paramref name="runId"/> is supplied, per-artifact completions
    /// are written to that run row as they land so the Press Run can poll progress
    /// from any instance while this call is still in flight (B9.8).
    /// </summary>
    Task<IReadOnlyList<GenerationResult>> RunFanOutAsync(Guid userId, Campaign campaign, TranscriptContent transcript, string? brief, string[]? kinds, CancellationToken ct, Guid? runId = null, int copies = 1);
    /// <summary>Opens a run row for progress polling (B9.8) before the fan-out starts.</summary>
    Task<Guid> StartRunAsync(Guid campaignId, string[]? kinds, CancellationToken ct, int copies = 1);

    /// <summary>
    /// Second pass over an existing artifact (ADR-020): a different model family re-edits it,
    /// optionally briefed by the customer knowledge base. Unlike every other AI path this
    /// revises the artifact <b>in place</b> behind a revision snapshot rather than printing a
    /// new row, so the version filmstrip shows the edit and can restore the take before it.
    /// </summary>
    Task<TechEditResult> RunTechEditAsync(
        Guid userId, Campaign campaign, Artifact artifact, TranscriptContent transcript,
        string? steering, bool useKnowledgeBase, CancellationToken ct);
}

public sealed class AiOrchestrator(
    IChatProviderRegistry chatProviders,
    IImagePlanService imagePlan,
    IBrandContextService brands,
    IKnowledgeBaseClient knowledge,
    IWorkspaceLinks workspaceLinks,
    CastmillDbContext db,
    ITenantProvider tenant,
    IPromptLog promptLog,
    TimeProvider clock,
    ILogger<AiOrchestrator> logger) : IAiOrchestrator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<GenerationResult>> RunFanOutAsync(
        Guid userId, Campaign campaign, TranscriptContent transcript, string? brief, string[]? kinds,
        CancellationToken ct, Guid? runId = null, int copies = 1)
    {
        copies = Math.Clamp(copies, 1, GenerateRequest.MaxCopies);
        kinds = kinds?.Select(Generators.Normalize).ToArray();
        var selected = kinds is { Length: > 0 }
            ? Generators.FanOut.Where(g => kinds.Contains(g.Kind, StringComparer.OrdinalIgnoreCase)).ToList()
            : [.. Generators.FanOut];

        // Kind-major rather than round-robin, so "3 more LinkedIn posts" arrives as three
        // LinkedIn posts in a row on the board rather than interleaved with everything else.
        var wanted = selected.SelectMany(spec => Enumerable.Repeat(spec, copies)).ToList();

        // Brand + campaign context resolve ONCE per run and steer every generator.
        var brand = await brands.ResolveAsync(campaign, ct);

        var results = new List<GenerationResult>();

        // The YouTube package prints FIRST — before the blog pipeline. It is the app's
        // founding deliverable, and parking it behind a minute of outline→draft→audit made
        // the most important artifact the one you waited longest for. Everything else keeps
        // registry order, and image-prompts still lands after the blog it seeds against.
        foreach (var spec in wanted.Where(w => w.Kind == "youtube"))
        {
            var result = await RunGeneratorCoreAsync(userId, campaign, transcript, brief, spec, brand, ct);
            results.Add(result);
            if (result is { Success: true, ArtifactId: { } artifactId })
            {
                await imagePlan.EnsureSlotsAsync(campaign.Id, ct, artifactId);
            }
            await RecordProgressAsync(runId, results, ct);
        }

        wanted = [.. wanted.Where(w => w.Kind != "youtube")];

        if (kinds is null || kinds.Length == 0 || kinds.Contains("blog", StringComparer.OrdinalIgnoreCase))
        {
            for (var copy = 0; copy < copies; copy++)
            {
                var blog = await RunBlogCoreAsync(userId, campaign, transcript, brief, brand, ct);
                results.Add(blog);
                if (blog is { Success: true, ArtifactId: { } blogId })
                {
                    await imagePlan.EnsureSlotsAsync(campaign.Id, ct, blogId);
                }
                await RecordProgressAsync(runId, results, ct);
            }
        }

        // Per-artifact granularity, partial failures allowed (ADR-006): one bad
        // generator never sinks the run. Sequential per DbContext (not thread-safe);
        // model-side latency dominates and the Press Run consumes per-artifact results.
        foreach (var spec in wanted)
        {
            var result = await RunGeneratorCoreAsync(userId, campaign, transcript, brief, spec, brand, ct);
            results.Add(result);

            if (result is { Success: true, ArtifactId: { } contentArtifactId }
                && result.Kind is not ("image-prompts" or "thumbnail-concepts" or "seo-brief"))
            {
                await imagePlan.EnsureSlotsAsync(campaign.Id, ct, contentArtifactId);
            }

            // Image prompts seed the reserved slots, keeping each prompt tied to the
            // transcript moment it illustrates.
            if (result is { Success: true, Kind: "image-prompts", ArtifactId: { } artifactId })
            {
                var artifact = await db.Artifacts.FindAsync([artifactId], ct);
                if (artifact is not null)
                {
                    // Seed each content item's own cards. Auto-mode cards that have no
                    // matching canned prompt rebuild from their owning artifact at render time.
                    foreach (var ownerId in results
                        .Where(r => r.Success && r.ArtifactId is not null
                            && r.Kind is not ("image-prompts" or "thumbnail-concepts" or "seo-brief"))
                        .Select(r => r.ArtifactId!.Value)
                        .Distinct())
                    {
                        await imagePlan.SeedPromptsAsync(campaign.Id, artifact.ContentJson, ct, ownerId);
                    }
                }
            }

            await RecordProgressAsync(runId, results, ct);
        }

        await CompleteRunAsync(runId, results, ct);
        return results;
    }

    /// <summary>Creates the run row the Press Run polls; returns its id.</summary>
    public async Task<Guid> StartRunAsync(Guid campaignId, string[]? kinds, CancellationToken ct, int copies = 1)
    {
        var total = (kinds is { Length: > 0 }
            ? kinds.Length
            : Generators.FanOut.Count + 1) // +1 for the blog pipeline
            * Math.Clamp(copies, 1, GenerateRequest.MaxCopies);
        var now = clock.GetUtcNow();
        var run = new GenerationRun
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId ?? throw new InvalidOperationException("Generation requires a tenant."),
            CampaignId = campaignId,
            Status = "Running",
            TotalKinds = total,
            ItemsJson = "[]",
            StartedAt = now,
            UpdatedAt = now,
        };
        db.GenerationRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run.Id;
    }

    private async Task RecordProgressAsync(Guid? runId, List<GenerationResult> results, CancellationToken ct)
    {
        if (runId is null)
        {
            return;
        }
        var run = await db.GenerationRuns.FindAsync([runId.Value], ct);
        if (run is null)
        {
            return;
        }
        run.ItemsJson = JsonSerializer.Serialize(results, Json);
        run.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private async Task CompleteRunAsync(Guid? runId, List<GenerationResult> results, CancellationToken ct)
    {
        if (runId is null)
        {
            return;
        }
        var run = await db.GenerationRuns.FindAsync([runId.Value], ct);
        if (run is null)
        {
            return;
        }
        run.ItemsJson = JsonSerializer.Serialize(results, Json);
        run.Status = "Completed";
        run.TotalKinds = results.Count;
        run.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task<GenerationResult> RunGeneratorAsync(
        Guid userId, Campaign campaign, TranscriptContent transcript, string? brief, GeneratorSpec spec, CancellationToken ct) =>
        await RunGeneratorCoreAsync(userId, campaign, transcript, brief, spec, await brands.ResolveAsync(campaign, ct), ct);

    private async Task<GenerationResult> RunGeneratorCoreAsync(
        Guid userId, Campaign campaign, TranscriptContent transcript, string? brief, GeneratorSpec spec, BrandContext brand, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await CallModelAsync(userId, "chat", spec.Kind,
                BuildPrompt(spec.Instructions, brief, transcript, brand, spec.Kind), ct);
            // Markers like [s03][s08] are for grounding, not for the copy someone
            // publishes; provenance stays in the citations array.
            var json = CitationMarkers.Strip(ParseModelJson(response));

            // Real URLs are substituted here, never written by the model. The prompt asks for
            // a {{LINKS}} placeholder precisely so a hallucinated link is impossible by
            // construction rather than by instruction.
            json = await SubstituteLinksAsync(userId, json, ct);

            // Deterministic pass before validation — clip in/out points are computed from
            // the transcript rather than taken from numbers the model wrote.
            if (spec.Transform is { } transform)
            {
                json = transform(json, transcript);
            }
            var validation = spec.Validate(json, transcript);
            if (!validation.Passed)
            {
                return Fail(spec.Kind, validation.FatalError!, stopwatch);
            }
            var artifactId = await PersistAsync(campaign, spec.Kind, json, validation, ct);
            return new GenerationResult(spec.Kind, true, artifactId, null, validation.Warnings, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Generator {Kind} failed", spec.Kind);
            return Fail(spec.Kind, ex is AiNotConfiguredException ? ex.Message : $"Generation failed: {ex.GetType().Name}", stopwatch);
        }
    }

    /// <summary>Blog pipeline (B5.2): outline → draft → cross-model audit.</summary>
    public async Task<GenerationResult> RunBlogAsync(
        Guid userId, Campaign campaign, TranscriptContent transcript, string? brief, CancellationToken ct) =>
        await RunBlogCoreAsync(userId, campaign, transcript, brief, await brands.ResolveAsync(campaign, ct), ct);

    private async Task<GenerationResult> RunBlogCoreAsync(
        Guid userId, Campaign campaign, TranscriptContent transcript, string? brief, BrandContext brand, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var outline = await CallModelAsync(userId, "chat", "blog-outline", BuildPrompt(
                """
                Create an outline for a long-form blog post from the source content.
                JSON schema: { "title": string, "sections": [ { "heading": string, "segmentIds": string[] } ], "citations": string[] }
                """, brief, transcript, brand, "blog"), ct);

            var draft = await CallModelAsync(userId, "chat", "blog-draft", BuildPrompt(
                $$"""
                Write the full blog post following this outline exactly:
                {{outline}}

                Target 1500-2500 words. Use markdown. Insert image stub markers like
                ![stub:blog-hero]() and ![stub:blog-inline-1]() where images belong.
                JSON schema: { "title": string, "markdown": string, "metaDescription": string, "citations": string[] }
                """, brief, transcript, brand, "blog"), ct);

            var draftJson = CitationMarkers.Strip(ParseModelJson(draft));
            var validation = Generators.ValidateBlog(draftJson, transcript);
            if (!validation.Passed)
            {
                return Fail("blog", validation.FatalError!, stopwatch);
            }

            // Cross-model audit: a second model (or the same one when chat-audit
            // is unmapped) checks the draft against the transcript for unsupported claims.
            var audit = await CallModelAsync(userId, "chat-audit", "blog-audit", BuildPrompt(
                $$"""
                You are auditing a blog draft against its source transcript. List any claims
                in the draft that the transcript does not support.
                Draft:
                {{draftJson.GetProperty("markdown").GetString()}}

                JSON schema: { "unsupportedClaims": [ { "claim": string, "reason": string } ], "citations": string[] }
                """, brief: null, transcript), ct);

            var warnings = new List<string>(validation.Warnings);
            try
            {
                var auditJson = ParseModelJson(audit);
                if (auditJson.TryGetProperty("unsupportedClaims", out var claims) && claims.ValueKind == JsonValueKind.Array)
                {
                    warnings.AddRange(claims.EnumerateArray()
                        .Where(c => c.TryGetProperty("claim", out _))
                        .Select(c => $"Audit: unsupported claim — {c.GetProperty("claim").GetString()}"));
                }
            }
            catch (JsonException)
            {
                warnings.Add("Audit pass returned unparseable output; review manually.");
            }

            var artifactId = await PersistAsync(campaign, "blog", draftJson,
                new ValidationOutcome(true, warnings), ct);
            return new GenerationResult("blog", true, artifactId, null, warnings, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Blog pipeline failed");
            return Fail("blog", ex is AiNotConfiguredException ? ex.Message : $"Generation failed: {ex.GetType().Name}", stopwatch);
        }
    }

    /// <summary>
    /// The Tech Edit (ADR-020). Same schema in, same schema out: the model is handed the
    /// artifact's own payload and must return that same shape edited, which is then run
    /// through the identical validator pass 1 had to satisfy. A second pass can therefore
    /// never produce an artifact the first pass would have rejected — and the contract works
    /// for every kind without a per-kind output schema.
    /// </summary>
    public async Task<TechEditResult> RunTechEditAsync(
        Guid userId, Campaign campaign, Artifact artifact, TranscriptContent transcript,
        string? steering, bool useKnowledgeBase, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var provider = "foundry";
        var knowledgeUsed = false;
        try
        {
            var kind = Generators.Normalize(artifact.Kind);
            var payload = ExtractContent(artifact.ContentJson);
            if (payload is null)
            {
                return TechEditFail(artifact, "This artifact has no readable content payload.", stopwatch);
            }

            var brand = await brands.ResolveAsync(campaign, ct);
            provider = await chatProviders.ResolveNameAsync(userId, FoundryClientFactory.TechEditAlias, ct);

            var knowledgeBlock = string.Empty;
            if (useKnowledgeBase)
            {
                var answer = await knowledge.AskAsync(userId, BuildKnowledgeQuery(artifact, payload.Value, campaign), ct);
                if (answer is not null)
                {
                    knowledgeUsed = true;
                    knowledgeBlock = $"\n{answer.ToPromptBlock()}\n";
                }
            }

            var instructions = $$"""
                You are the technical editor on this artifact. It has already been drafted and
                validated; your job is to make it more accurate, more specific and more useful
                to a technical reader — not to rewrite it for the sake of rewriting.

                Correct anything the knowledge base contradicts, replace vague claims with
                concrete ones it supports, and link to a source URL where one genuinely backs a
                statement. Leave the structure, length and voice alone unless they are the
                problem. If a passage is already right, return it unchanged.

                Return the SAME JSON schema you are given, edited, wrapped like this:
                { "artifact": { ...the same shape as the current content... },
                  "changes": [ { "what": string, "why": string, "sourceUrl": string } ] }

                Every field present in the current content must still be present, including
                "citations", which must keep citing real transcript segment ids.
                {{knowledgeBlock}}
                Current content:
                {{payload.Value.GetRawText()}}
                """;

            var response = await CallModelAsync(userId, FoundryClientFactory.TechEditAlias,
                $"{kind}-tech-edit", BuildPrompt(instructions, steering, transcript, brand, kind), ct);

            var parsed = CitationMarkers.Strip(ParseModelJson(response));
            if (!parsed.TryGetProperty("artifact", out var edited) || edited.ValueKind != JsonValueKind.Object)
            {
                return TechEditFail(artifact, "The tech edit returned no artifact payload.", stopwatch);
            }

            var validation = Validate(kind, edited, transcript);
            if (!validation.Passed)
            {
                // The draft on disk is still the validated one; refusing to write is the
                // whole point of running the same validator pass 1 used.
                return TechEditFail(artifact, $"Tech edit rejected by validation: {validation.FatalError}", stopwatch);
            }

            var changes = ReadChanges(parsed);
            var warnings = new List<string>(validation.Warnings);
            warnings.AddRange(changes.Select(c => $"Tech edit: {c}"));

            var now = clock.GetUtcNow();
            await Endpoints.ArtifactEndpoints.SnapshotRevisionAsync(db, artifact, "tech-edit", now, ct);

            artifact.ContentJson = JsonSerializer.Serialize(new
            {
                content = edited,
                validation = new { validation.Passed, Warnings = warnings },
            }, Json);
            if (edited.TryGetProperty("title", out var title)
                && title.ValueKind == JsonValueKind.String
                && title.GetString() is { Length: > 0 } titleText)
            {
                artifact.Title = titleText.Length > 300 ? titleText[..300] : titleText;
            }
            artifact.Version++;
            artifact.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            return new TechEditResult(true, null, artifact.Id, artifact.Version, provider, knowledgeUsed,
                changes, warnings, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Tech edit failed for artifact {ArtifactId}", artifact.Id);
            return TechEditFail(artifact,
                ex is AiNotConfiguredException ? ex.Message : $"Tech edit failed: {ex.GetType().Name}",
                stopwatch, provider, knowledgeUsed);
        }
    }

    /// <summary>Runs the kind's own pass-1 validator; blog has its own pipeline and validator.</summary>
    private static ValidationOutcome Validate(string kind, JsonElement json, TranscriptContent transcript) =>
        kind.Equals("blog", StringComparison.OrdinalIgnoreCase)
            ? Generators.ValidateBlog(json, transcript)
            : Generators.Find(kind) is { } spec
                ? spec.Validate(json, transcript)
                // An unregistered kind (hand-authored, or one that predates the registry) still
                // gets the common contract rather than being waved through unchecked.
                : Generators.ValidateCommon(json, transcript);

    /// <summary>
    /// Unwraps the orchestrator's <c>{ content, validation }</c> envelope. Hand-authored
    /// payloads keep their fields at the top level, so both shapes have to be handled — the
    /// same split <c>ArtifactContent.FindMarkdownHost</c> makes on the client.
    /// </summary>
    internal static JsonElement? ExtractContent(string contentJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(contentJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return root.TryGetProperty("content", out var inner) && inner.ValueKind == JsonValueKind.Object
                ? inner.Clone()
                : root.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The question put to the knowledge base. Built from what the artifact is ABOUT — title
    /// plus meta description plus headings — rather than its full body, which would bury the
    /// topic in prose the gateway has to re-summarise.
    /// </summary>
    private static string BuildKnowledgeQuery(Artifact artifact, JsonElement payload, Campaign campaign)
    {
        var parts = new List<string> { artifact.Title };
        if (payload.TryGetProperty("metaDescription", out var meta) && meta.ValueKind == JsonValueKind.String)
        {
            parts.Add(meta.GetString()!);
        }
        if (payload.TryGetProperty("markdown", out var markdown) && markdown.ValueKind == JsonValueKind.String)
        {
            parts.AddRange(markdown.GetString()!
                .Split('\n')
                .Where(line => line.StartsWith("## ", StringComparison.Ordinal))
                .Select(line => line[3..].Trim())
                .Take(8));
        }
        parts.Add(campaign.Name);

        var query = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct());
        return query.Length > 900 ? query[..900] : query;
    }

    private static IReadOnlyList<string> ReadChanges(JsonElement parsed)
    {
        if (!parsed.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. changes.EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("what", out _))
            .Select(c =>
            {
                var what = c.GetProperty("what").GetString() ?? string.Empty;
                var why = c.TryGetProperty("why", out var w) ? w.GetString() : null;
                var source = c.TryGetProperty("sourceUrl", out var s) ? s.GetString() : null;
                var text = string.IsNullOrWhiteSpace(why) ? what : $"{what} — {why}";
                return string.IsNullOrWhiteSpace(source) ? text : $"{text} ({source})";
            })
            .Where(t => t.Length > 0)];
    }

    private static TechEditResult TechEditFail(
        Artifact artifact, string error, Stopwatch stopwatch,
        string provider = "foundry", bool knowledgeUsed = false) =>
        new(false, error, artifact.Id, artifact.Version, provider, knowledgeUsed, [], [],
            stopwatch.ElapsedMilliseconds);

    // ---- Internals -----------------------------------------------------------

    /// <summary>
    /// The ONE place prompt text is assembled. Order matters and is deliberate: contract →
    /// generator instructions → per-brand template steering (rides WITH the instructions) →
    /// campaign brief → brand style block → campaign context links → SEO/AEO targets →
    /// source transcript.
    /// Labeled sections let the model distinguish contract vs steering vs facts.
    /// </summary>
    private static string BuildPrompt(
        string instructions, string? brief, TranscriptContent transcript,
        BrandContext? brand = null, string? kind = null)
    {
        var steering = kind is not null
            && brand?.TemplateSteeringByKind.TryGetValue(kind, out var template) == true
                ? $"\nBrand template steering: {template}\n"
                : string.Empty;
        var styleBlock = string.IsNullOrWhiteSpace(brand?.StyleBlock) ? string.Empty : $"{brand!.StyleBlock}\n";
        var contextBlock = string.IsNullOrWhiteSpace(brand?.CampaignContextBlock)
            ? string.Empty
            : $"{brand!.CampaignContextBlock}\n";

        // LAST of the steering blocks, immediately before the transcript: these are the
        // targets the piece is being written to hit, and the nearest instruction to the
        // source is the one a model weights most heavily.
        var seoBlock = string.IsNullOrWhiteSpace(brand?.SeoTargetBlock)
            ? string.Empty
            : $"{brand!.SeoTargetBlock}\n";

        return $"""
        {Generators.CommonContract}

        {instructions}
        {steering}
        {(string.IsNullOrWhiteSpace(brief) ? "" : $"Campaign brief: {brief}\n")}
        {styleBlock}{contextBlock}{seoBlock}
        Source transcript. Ground every claim in it and list the ids you used in the
        "citations" array — but NEVER write those ids into the prose itself: the body is
        published copy, and "[s03][s08]" in the middle of a sentence is a defect.
        {TranscriptService.ToPromptText(transcript)}
        """;
    }

    /// <summary>
    /// Replaces the <c>{{LINKS}}</c> placeholder with the workspace's configured URLs. When no
    /// links are set the placeholder is removed rather than left in the copy — a visible
    /// "{{LINKS}}" in a published YouTube description would be worse than no link block.
    /// </summary>
    private async Task<JsonElement> SubstituteLinksAsync(Guid userId, JsonElement json, CancellationToken ct)
    {
        var raw = json.GetRawText();
        if (!raw.Contains("{{LINKS}}", StringComparison.Ordinal))
        {
            return json;
        }

        var block = await workspaceLinks.RenderBlockAsync(userId, ct);

        // Substituted on the ENCODED text so newlines in the block stay valid JSON.
        var encoded = JsonEncodedText.Encode(block).ToString();
        return ParseModelJson(raw.Replace("{{LINKS}}", encoded, StringComparison.Ordinal));
    }

    private async Task<string> CallModelAsync(Guid userId, string modelAlias, string kind, string prompt, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var success = false;
        var responseText = string.Empty;
        try
        {
            // Through the registry, not the Foundry factory directly: an alias mapped to a
            // ready non-Foundry text provider resolves there, everything else stays on
            // Foundry. The log entry below therefore covers both providers (ADR-020).
            var client = await chatProviders.ResolveAsync(userId, modelAlias, ct);
            var response = await client.GetResponseAsync(prompt, cancellationToken: ct);
            responseText = response.Text;
            success = true;
            return responseText;
        }
        finally
        {
            promptLog.Record(new PromptLogEntry(
                clock.GetUtcNow(), userId, kind, modelAlias,
                Excerpt(prompt), Excerpt(responseText), success, stopwatch.ElapsedMilliseconds));
        }
    }

    /// <summary>Parses model output as strict JSON, tolerating a fenced code block.</summary>
    internal static JsonElement ParseModelJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n', StringComparison.Ordinal);
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
            }
        }
        using var doc = JsonDocument.Parse(trimmed);
        return doc.RootElement.Clone();
    }

    private async Task<Guid> PersistAsync(
        Campaign campaign, string kind, JsonElement content, ValidationOutcome validation, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var title = content.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()!
            : kind;

        var envelope = JsonSerializer.Serialize(new
        {
            content,
            validation = new { validation.Passed, validation.Warnings },
        }, Json);

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId ?? throw new InvalidOperationException("Generation requires a tenant."),
            CampaignId = campaign.Id,
            Kind = kind,
            Title = title.Length > 300 ? title[..300] : title,
            ContentJson = envelope,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Artifacts.Add(artifact);
        await db.SaveChangesAsync(ct);
        return artifact.Id;
    }

    private static GenerationResult Fail(string kind, string error, Stopwatch stopwatch) =>
        new(kind, false, null, error, [], stopwatch.ElapsedMilliseconds);

    private static string Excerpt(string value) =>
        value.Length <= PromptLog.ExcerptLength ? value : value[..PromptLog.ExcerptLength];
}
