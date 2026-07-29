using System.Diagnostics;
using System.Text.Json;
using Castmill.Api.Data;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Ai;
using Microsoft.Extensions.AI;

namespace Castmill.Api.Services.Ai;

public interface IAiOrchestrator
{
    Task<GenerationResult> RunBlogAsync(Guid userId, Campaign campaign, TranscriptContent transcript, string? brief, CancellationToken ct);
    Task<GenerationResult> RunGeneratorAsync(Guid userId, Campaign campaign, TranscriptContent transcript, string? brief, GeneratorSpec spec, CancellationToken ct);
    Task<IReadOnlyList<GenerationResult>> RunFanOutAsync(Guid userId, Campaign campaign, TranscriptContent transcript, string? brief, string[]? kinds, CancellationToken ct);
}

public sealed class AiOrchestrator(
    IFoundryClientFactory clients,
    CastmillDbContext db,
    ITenantProvider tenant,
    IPromptLog promptLog,
    TimeProvider clock,
    ILogger<AiOrchestrator> logger) : IAiOrchestrator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<GenerationResult>> RunFanOutAsync(
        Guid userId, Campaign campaign, TranscriptContent transcript, string? brief, string[]? kinds, CancellationToken ct)
    {
        var wanted = kinds is { Length: > 0 }
            ? Generators.FanOut.Where(g => kinds.Contains(g.Kind, StringComparer.OrdinalIgnoreCase)).ToList()
            : [.. Generators.FanOut];

        var results = new List<GenerationResult>();
        if (kinds is null || kinds.Length == 0 || kinds.Contains("blog", StringComparer.OrdinalIgnoreCase))
        {
            results.Add(await RunBlogAsync(userId, campaign, transcript, brief, ct));
        }

        // Per-artifact granularity, partial failures allowed (ADR-006): one bad
        // generator never sinks the run. Sequential per DbContext (not thread-safe);
        // model-side latency dominates and the Press Run consumes per-artifact results.
        foreach (var spec in wanted)
        {
            results.Add(await RunGeneratorAsync(userId, campaign, transcript, brief, spec, ct));
        }
        return results;
    }

    public async Task<GenerationResult> RunGeneratorAsync(
        Guid userId, Campaign campaign, TranscriptContent transcript, string? brief, GeneratorSpec spec, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await CallModelAsync(userId, "chat", spec.Kind, BuildPrompt(spec.Instructions, brief, transcript), ct);
            var json = ParseModelJson(response);
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
        Guid userId, Campaign campaign, TranscriptContent transcript, string? brief, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var outline = await CallModelAsync(userId, "chat", "blog-outline", BuildPrompt(
                """
                Create an outline for a long-form blog post from the source content.
                JSON schema: { "title": string, "sections": [ { "heading": string, "segmentIds": string[] } ], "citations": string[] }
                """, brief, transcript), ct);

            var draft = await CallModelAsync(userId, "chat", "blog-draft", BuildPrompt(
                $$"""
                Write the full blog post following this outline exactly:
                {{outline}}

                Target 1500-2500 words. Use markdown. Insert image stub markers like
                ![stub:blog-hero]() and ![stub:blog-inline-1]() where images belong.
                JSON schema: { "title": string, "markdown": string, "metaDescription": string, "citations": string[] }
                """, brief, transcript), ct);

            var draftJson = ParseModelJson(draft);
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

    // ---- Internals -----------------------------------------------------------

    private static string BuildPrompt(string instructions, string? brief, TranscriptContent transcript) =>
        $"""
        {Generators.CommonContract}

        {instructions}

        {(string.IsNullOrWhiteSpace(brief) ? "" : $"Campaign brief: {brief}\n")}
        Source transcript (cite segment ids in square brackets):
        {TranscriptService.ToPromptText(transcript)}
        """;

    private async Task<string> CallModelAsync(Guid userId, string modelAlias, string kind, string prompt, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var success = false;
        var responseText = string.Empty;
        try
        {
            var client = await clients.CreateChatClientAsync(userId, modelAlias, ct);
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
