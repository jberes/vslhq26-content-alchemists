using System.Text.Json;
using Castmill.Api.Data;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Services.Images;

/// <summary>One reserved slot's shape: what it is and the exact pixels it must end up at.</summary>
public sealed record SlotTemplate(string Kind, int Width, int Height, string AspectRatio, bool Headline);

public interface IImagePlanService
{
    /// <summary>
    /// Reserves the campaign's six typed slots (ADR-012). Idempotent: re-running a
    /// campaign never duplicates or resets slots that already hold an image.
    /// </summary>
    Task<IReadOnlyList<ImageSlot>> EnsureSlotsAsync(Guid campaignId, CancellationToken ct);

    /// <summary>
    /// Copies prompts from a generated image-prompts artifact onto empty slots,
    /// keeping the transcript segment that motivated each one. Never overwrites a
    /// prompt the user edited on a filled slot.
    /// </summary>
    Task<int> SeedPromptsAsync(Guid campaignId, string imagePromptsContentJson, CancellationToken ct);
}

public sealed class ImagePlanService(
    CastmillDbContext db,
    ITenantProvider tenant,
    TimeProvider clock) : IImagePlanService
{
    /// <summary>
    /// The plan is fixed in v1 (ADR-012 "revisit when slots become user-definable").
    /// Sizes are the platform-correct ones, not whatever the model happens to emit —
    /// the render path crops to these exactly (B9.2).
    /// </summary>
    public static readonly SlotTemplate[] Templates =
    [
        new("youtube-thumbnail", 1280, 720, "16:9", Headline: true),
        new("blog-header", 1600, 840, "16:9", Headline: false),
        new("blog-inline-1", 1200, 675, "16:9", Headline: false),
        new("blog-inline-2", 1200, 675, "16:9", Headline: false),
        new("blog-inline-3", 1200, 675, "16:9", Headline: false),
        new("social-card", 1200, 1200, "1:1", Headline: false),
    ];

    public static SlotTemplate? Template(string kind) =>
        Templates.FirstOrDefault(t => t.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<ImageSlot>> EnsureSlotsAsync(Guid campaignId, CancellationToken ct)
    {
        var existing = await db.ImageSlots.Where(s => s.CampaignId == campaignId).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var added = false;

        foreach (var template in Templates)
        {
            if (existing.Any(s => s.Kind.Equals(template.Kind, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var slot = new ImageSlot
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId ?? throw new InvalidOperationException("Slot reservation requires a tenant."),
                CampaignId = campaignId,
                Kind = template.Kind,
                TargetWidth = template.Width,
                TargetHeight = template.Height,
                SafeArea = template.Headline,
                State = "Empty",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ImageSlots.Add(slot);
            existing.Add(slot);
            added = true;
        }

        if (added)
        {
            await db.SaveChangesAsync(ct);
        }
        return [.. existing.OrderBy(s => Array.FindIndex(Templates, t => t.Kind == s.Kind))];
    }

    public async Task<int> SeedPromptsAsync(Guid campaignId, string imagePromptsContentJson, CancellationToken ct)
    {
        var prompts = ParsePrompts(imagePromptsContentJson);
        if (prompts.Count == 0)
        {
            return 0;
        }

        var slots = await EnsureSlotsAsync(campaignId, ct);
        var now = clock.GetUtcNow();
        var seeded = 0;

        foreach (var slot in slots)
        {
            // A filled slot's prompt is the user's; only empty slots get seeded.
            if (slot.State != "Empty")
            {
                continue;
            }
            if (!prompts.TryGetValue(MapPromptSlot(slot.Kind), out var prompt))
            {
                continue;
            }
            slot.Prompt = prompt.Prompt;
            slot.SourceSegmentId = prompt.SegmentId ?? slot.SourceSegmentId;
            slot.UpdatedAt = now;
            seeded++;
        }

        if (seeded > 0)
        {
            await db.SaveChangesAsync(ct);
        }
        return seeded;
    }

    /// <summary>The generator's slot vocabulary predates the plan; map it onto slot kinds.</summary>
    internal static string MapPromptSlot(string slotKind) => slotKind.ToLowerInvariant() switch
    {
        "blog-header" => "blog-hero",
        _ => slotKind.ToLowerInvariant(),
    };

    internal static Dictionary<string, (string Prompt, string? SegmentId)> ParsePrompts(string contentJson)
    {
        var results = new Dictionary<string, (string, string?)>(StringComparer.OrdinalIgnoreCase);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(contentJson);
        }
        catch (JsonException)
        {
            return results;
        }
        using (doc)
        {
            var content = doc.RootElement.TryGetProperty("content", out var c) ? c : doc.RootElement;
            if (!content.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            {
                return results;
            }
            // Fall back to the artifact's own citations when an image doesn't name one.
            var fallbackSegment = content.TryGetProperty("citations", out var cites)
                && cites.ValueKind == JsonValueKind.Array
                    ? cites.EnumerateArray().FirstOrDefault(x => x.ValueKind == JsonValueKind.String).GetString()
                    : null;

            foreach (var image in images.EnumerateArray())
            {
                if (!image.TryGetProperty("slot", out var slot) || slot.ValueKind != JsonValueKind.String
                    || !image.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var segment = image.TryGetProperty("segmentId", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString()
                    : fallbackSegment;
                results[slot.GetString()!] = (prompt.GetString()!, segment);
            }
        }
        return results;
    }
}
