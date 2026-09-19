using System.Security.Cryptography;
using System.Text;
using Castmill.Api.Data;
using Castmill.Api.Services.Images;
using Castmill.Core;

namespace Castmill.Api.Services.Ai;

public interface IImagePromptBuilder
{
    /// <summary>
    /// The effective prompt for a slot (ADR-075). Manual mode: the producer's text, composed as
    /// before. Auto mode: an AI-written visual brief, cached on the slot until its inputs
    /// change, falling back to the deterministic composer if the writer cannot run.
    /// </summary>
    Task<string> BuildAsync(
        Guid userId, ImageSlot slot, Campaign campaign, Artifact? owner, BrandContext brand,
        IReadOnlyList<ImageReference>? references, bool textMayBeRendered, CancellationToken ct);

    /// <summary>Stores a producer-edited Auto brief against the same input fingerprint used
    /// by generated briefs, so it remains authoritative until those inputs really change.</summary>
    void SetBrief(
        ImageSlot slot, Campaign campaign, Artifact? owner, BrandContext brand,
        IReadOnlyList<ImageReference>? references, bool textMayBeRendered, string brief);

    /// <summary>Forget the cached brief so the next build writes a fresh one.</summary>
    void Invalidate(ImageSlot slot);
}

public sealed class ImagePromptBuilder(
    IVisualBriefWriter writer,
    CastmillDbContext db,
    ILogger<ImagePromptBuilder> logger) : IImagePromptBuilder
{
    public async Task<string> BuildAsync(
        Guid userId, ImageSlot slot, Campaign campaign, Artifact? owner, BrandContext brand,
        IReadOnlyList<ImageReference>? references, bool textMayBeRendered, CancellationToken ct)
    {
        if (string.Equals(slot.PromptMode, "Manual", StringComparison.OrdinalIgnoreCase))
        {
            return ImagePromptComposer.Compose(slot, campaign, owner, brand, null, references);
        }

        var request = Request(slot, campaign, owner, brand, references, textMayBeRendered);
        var hash = Hash(request, owner);

        if (slot.VisualBrief is { Length: > 0 } cached && string.Equals(slot.VisualBriefInputHash, hash, StringComparison.Ordinal))
        {
            return ImagePromptComposer.FromBrief(cached, references);
        }

        var brief = await writer.WriteAsync(userId, request, ct);
        if (brief is null)
        {
            logger.LogWarning("No visual brief for slot {SlotId}; composing deterministically", slot.Id);
            // Cache the deterministic fallback as the raw brief too. That keeps it editable
            // and lets the normal FromBrief path add reference roles exactly once.
            brief = ImagePromptComposer.Compose(slot, campaign, owner, brand, null, references: null);
        }

        slot.VisualBrief = brief;
        slot.VisualBriefInputHash = hash;
        // The slot may be tracked by the caller's query or not; attaching by state is safe either way.
        if (db.Entry(slot).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
        {
            db.ImageSlots.Attach(slot);
        }
        db.Entry(slot).Property(s => s.VisualBrief).IsModified = true;
        db.Entry(slot).Property(s => s.VisualBriefInputHash).IsModified = true;
        await db.SaveChangesAsync(ct);
        return ImagePromptComposer.FromBrief(brief, references);
    }

    public void SetBrief(
        ImageSlot slot, Campaign campaign, Artifact? owner, BrandContext brand,
        IReadOnlyList<ImageReference>? references, bool textMayBeRendered, string brief)
    {
        var request = Request(slot, campaign, owner, brand, references, textMayBeRendered);
        slot.VisualBrief = brief.Trim();
        slot.VisualBriefInputHash = Hash(request, owner);
    }

    public void Invalidate(ImageSlot slot)
    {
        slot.VisualBrief = null;
        slot.VisualBriefInputHash = null;
    }

    private static VisualBriefRequest Request(
        ImageSlot slot, Campaign campaign, Artifact? owner, BrandContext brand,
        IReadOnlyList<ImageReference>? references, bool textMayBeRendered) =>
        new(
            EffectiveSlotKind(slot, owner), slot.TargetWidth, slot.TargetHeight,
            Subject: owner?.Title ?? campaign.Name,
            ContentDigest: ImagePromptComposer.ContentDigest(owner?.ContentJson),
            CampaignBrief: campaign.Brief,
            CreativeDirection: slot.Prompt,
            BrandLook: brand.ImageStyleBlock,
            Audience: campaign.AudiencePersona,
            ReferenceKinds: [.. (references ?? []).Select(r => r.Kind)],
            TextMayBeRendered: textMayBeRendered,
            HeadlineWillBeComposited: !string.IsNullOrWhiteSpace(slot.HeadlineText)
                || (ImagePromptRules.IsTextFirst(slot.Kind) && !textMayBeRendered),
            BrandContentTemplate: TemplateFor(slot, owner, brand));

    /// <summary>Legacy YouTube rows were named content-image-N. Their owning artifact is
    /// authoritative: they still receive thumbnail title, layout and brand-template rules.</summary>
    internal static string EffectiveSlotKind(ImageSlot slot, Artifact? owner) =>
        owner?.Kind.Equals("youtube", StringComparison.OrdinalIgnoreCase) == true
            ? "youtube-thumbnail"
            : slot.Kind;

    internal static string? TemplateFor(ImageSlot slot, Artifact? owner, BrandContext brand)
    {
        var kind = slot.Kind.Equals("youtube-thumbnail", StringComparison.OrdinalIgnoreCase)
            ? "youtube"
            : owner?.Kind;
        return kind is not null && brand.TemplateSteeringByKind.TryGetValue(kind, out var template)
            ? template
            : null;
    }

    /// <summary>Everything the brief was written from. The owner's version stands in for its content.</summary>
    internal static string Hash(VisualBriefRequest r, Artifact? owner)
    {
        var text = string.Join('\u001f',
            r.SlotKind, r.TargetWidth, r.TargetHeight, r.Subject, owner?.Version, owner?.UpdatedAt.UtcTicks,
            r.CampaignBrief, r.CreativeDirection, r.BrandLook, r.Audience,
            string.Join(',', r.ReferenceKinds), r.TextMayBeRendered, r.HeadlineWillBeComposited,
            r.BrandContentTemplate);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }
}
