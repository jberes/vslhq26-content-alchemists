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
/// An image pasted or dropped into the editor. Base64 rather than multipart because it
/// arrives through a JS-interop hop, which cannot hand .NET a stream.
/// </summary>
public sealed record ImageUploadRequest(
    [property: Required, MaxLength(260)] string FileName,
    [property: Required, MaxLength(100)] string ContentType,
    /// <summary>Bytes, base64, no data: prefix. Capped at ~11 MB encoded (8 MB decoded).</summary>
    [property: Required, MaxLength(12_000_000)] string Base64);

public sealed record ImageUploadResponse(string Url);

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
        // Editor-side upload: an image pasted or dropped into the manuscript. Not on the
        // "ai" partition — this costs storage, not model spend.
        routes.MapPost("/api/v1/campaigns/{campaignId:guid}/images/upload", UploadAsync)
            .Validate<ImageUploadRequest>()
            .RequireAuthorization("TenantAllowed")
            .RequireRateLimiting("writes");
        return routes;
    }

    /// <summary>Largest image the editor will accept. Base64 inflates the body by ~33%.</summary>
    internal const int MaxUploadBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Content types we will publish, each paired with the magic bytes that prove it. The
    /// declared type is never trusted: this writes to a PUBLIC container, so a mislabelled
    /// upload would be served to anyone with the URL as whatever the sniffer decides it is.
    /// </summary>
    private static readonly (string ContentType, string Extension, byte[] Magic)[] AllowedImages =
    [
        ("image/webp", "webp", [0x52, 0x49, 0x46, 0x46]),   // "RIFF" (…WEBP at offset 8)
        ("image/png", "png", [0x89, 0x50, 0x4E, 0x47]),
        ("image/jpeg", "jpg", [0xFF, 0xD8, 0xFF]),
        ("image/gif", "gif", [0x47, 0x49, 0x46, 0x38]),
    ];

    private static async Task<IResult> UploadAsync(
        Guid campaignId,
        ImageUploadRequest request,
        IPublicContentStore publicStore,
        CastmillDbContext db,
        CancellationToken ct)
    {
        if (!publicStore.IsConfigured)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "No public content container is configured, so images cannot be published.");
        }

        // Tenant-filtered by the global query filter — an id from another tenant is a 404.
        if (!await db.Campaigns.AnyAsync(c => c.Id == campaignId, ct))
        {
            return Results.NotFound();
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(request.Base64);
        }
        catch (FormatException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Image data is not valid base64.");
        }

        if (bytes.Length == 0 || bytes.Length > MaxUploadBytes)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                detail: $"Images must be between 1 byte and {MaxUploadBytes / (1024 * 1024)} MB.");
        }

        var match = AllowedImages.FirstOrDefault(a => StartsWith(bytes, a.Magic));
        if (match.ContentType is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "Only PNG, JPEG, GIF and WebP images can be inserted.");
        }

        // The blob name comes from a GUID, never the user's filename: a filename reaches a
        // public URL, and sanitizing one correctly is a losing game.
        var path = $"campaigns/{campaignId}/images/uploads/{Guid.NewGuid():N}.{match.Extension}";
        var url = await publicStore.PublishAsync(path, bytes, match.ContentType, ct);

        return Results.Ok(new ImageUploadResponse(url.ToString()));
    }

    private static bool StartsWith(byte[] bytes, byte[] magic) =>
        bytes.Length >= magic.Length && bytes.AsSpan(0, magic.Length).SequenceEqual(magic);

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
