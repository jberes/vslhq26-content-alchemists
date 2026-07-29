using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

public sealed record RenderImagesRequest(
    [property: Required] Guid ImagePromptsArtifactId,
    /// <summary>Blog artifact whose ![stub:slot]() markers get replaced. Optional — omit to only publish images.</summary>
    Guid? BlogArtifactId,
    /// <summary>Model alias to render with; defaults to "image" (gpt-image-2), "image-alt" is the MAI deployment.</summary>
    string? ModelAlias);

public sealed record RenderedImage(string Slot, string Url);

/// <summary>
/// B5.4: image-prompts artifact → generated WebP in the public container →
/// blog ![stub:slot]() markers replaced with real URLs.
/// </summary>
public static class ImageEndpoints
{
    public static IEndpointRouteBuilder MapImageEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/ai/campaigns/{campaignId:guid}/render-images", RenderAsync)
            .Validate<RenderImagesRequest>()
            .RequireAuthorization("TenantAllowed")
            .RequireRateLimiting("ai");
        return routes;
    }

    private static async Task<IResult> RenderAsync(
        Guid campaignId,
        RenderImagesRequest request,
        ClaimsPrincipal principal,
        IImageRenderer renderer,
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
        if (!await db.Campaigns.AnyAsync(c => c.Id == campaignId, ct))
        {
            return Results.NotFound();
        }

        var promptsArtifact = await db.Artifacts.SingleOrDefaultAsync(
            a => a.Id == request.ImagePromptsArtifactId && a.CampaignId == campaignId && a.Kind == "image-prompts", ct);
        if (promptsArtifact is null)
        {
            return Results.NotFound();
        }

        var images = ExtractImagePrompts(promptsArtifact.ContentJson);
        if (images.Count == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["ImagePromptsArtifactId"] = ["The image-prompts artifact contains no images."],
            });
        }

        var userId = AuthEndpoints.GetUserId(principal);
        var alias = string.IsNullOrWhiteSpace(request.ModelAlias) ? "image" : request.ModelAlias;
        var rendered = new List<RenderedImage>();
        var failures = new List<string>();

        foreach (var (slot, prompt, aspectRatio) in images)
        {
            try
            {
                var webp = await renderer.RenderWebpAsync(userId, prompt, aspectRatio, alias, ct);
                var url = await publicStore.PublishAsync(
                    $"campaigns/{campaignId}/images/{promptsArtifact.Id}/{slot}.webp",
                    webp, "image/webp", ct);
                rendered.Add(new RenderedImage(slot, url.ToString()));
            }
            catch (AiNotConfiguredException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{slot}: {ex.GetType().Name}"); // partial failure never sinks the run
            }
        }

        long? blogVersion = null;
        if (request.BlogArtifactId is { } blogId && rendered.Count > 0)
        {
            var blog = await db.Artifacts.SingleOrDefaultAsync(
                a => a.Id == blogId && a.CampaignId == campaignId && a.Kind == "blog", ct);
            if (blog is null)
            {
                return Results.NotFound();
            }
            if (ReplaceStubs(blog.ContentJson, rendered) is { } updated)
            {
                blog.ContentJson = updated;
                blog.Version++;
                blog.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }
            blogVersion = blog.Version;
        }

        return Results.Ok(new { rendered, failures, blogVersion });
    }

    internal static List<(string Slot, string Prompt, string AspectRatio)> ExtractImagePrompts(string contentJson)
    {
        var results = new List<(string, string, string)>();
        using var doc = JsonDocument.Parse(contentJson);
        // Generated artifacts persist as { content: {...}, validation: {...} }.
        var content = doc.RootElement.TryGetProperty("content", out var c) ? c : doc.RootElement;
        if (!content.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return results;
        }
        foreach (var image in images.EnumerateArray())
        {
            if (image.TryGetProperty("slot", out var slot) && image.TryGetProperty("prompt", out var prompt))
            {
                var aspect = image.TryGetProperty("aspectRatio", out var a) && a.ValueKind == JsonValueKind.String
                    ? a.GetString()! : "1:1";
                results.Add((slot.GetString()!, prompt.GetString()!, aspect));
            }
        }
        return results;
    }

    /// <summary>Replaces ![stub:slot]() markers in the blog markdown; null when nothing changed.</summary>
    internal static string? ReplaceStubs(string blogContentJson, IReadOnlyList<RenderedImage> rendered)
    {
        var root = JsonNode.Parse(blogContentJson)!;
        var content = root["content"] ?? root;
        if (content["markdown"] is not JsonValue markdownValue)
        {
            return null;
        }
        var markdown = markdownValue.GetValue<string>();
        var changed = false;
        foreach (var image in rendered)
        {
            var stub = $"![stub:{image.Slot}]()";
            if (markdown.Contains(stub, StringComparison.Ordinal))
            {
                markdown = markdown.Replace(stub, $"![{image.Slot}]({image.Url})", StringComparison.Ordinal);
                changed = true;
            }
        }
        if (!changed)
        {
            return null;
        }
        content["markdown"] = markdown;
        return root.ToJsonString();
    }
}
