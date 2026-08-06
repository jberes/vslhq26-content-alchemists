using System.Text;
using Castmill.Api.Data;
using Castmill.Api.Endpoints;
using Castmill.Core;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Services.Ai;

/// <summary>Everything brand- and campaign-context-shaped a generation run needs, resolved
/// once per run. Blocks are pre-rendered prompt text; empty context has every member null.</summary>
public sealed record BrandContext(
    string? StyleBlock,
    string? ImageStyleBlock,
    IReadOnlyDictionary<string, string> TemplateSteeringByKind,
    string? CampaignContextBlock)
{
    public static readonly BrandContext Empty = new(null, null,
        new Dictionary<string, string>(StringComparer.Ordinal), null);
}

public interface IBrandContextService
{
    Task<BrandContext> ResolveAsync(Campaign campaign, CancellationToken ct);
}

/// <summary>
/// The ONE place brand steering becomes prompt text (G4's "all AI behind one seam",
/// applied to brands). Text generators get the style block + per-kind default template;
/// image generation gets the image block; campaign links become a labeled facts section.
/// A future URL scraper would enrich <see cref="BuildCampaignContextBlock"/> — nothing else.
/// </summary>
public sealed class BrandContextService(CastmillDbContext db) : IBrandContextService
{
    public async Task<BrandContext> ResolveAsync(Campaign campaign, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var contextBlock = BuildCampaignContextBlock(CampaignEndpoints.ParseLinks(campaign.ContextJson));

        if (campaign.BrandId is not { } brandId)
        {
            return BrandContext.Empty with { CampaignContextBlock = contextBlock };
        }

        var brand = await db.BrandProfiles.SingleOrDefaultAsync(b => b.Id == brandId, ct);
        if (brand is null)
        {
            return BrandContext.Empty with { CampaignContextBlock = contextBlock };
        }

        var card = BrandEndpoints.ParseStyleCard(brand.StyleCardJson);

        var templates = await db.BrandTemplates
            .Where(t => t.BrandId == brandId && t.IsDefault)
            .ToDictionaryAsync(t => t.Kind, t => t.SteeringPrompt, StringComparer.Ordinal, ct);

        var imageAssets = await db.BrandAssets
            .Where(a => a.BrandId == brandId
                && (a.Kind == "background" || a.Kind == "face")
                && a.Label != null)
            .Select(a => new { a.Kind, a.Label })
            .ToListAsync(ct);

        return new BrandContext(
            BuildStyleBlock(brand.Name, card),
            BuildImageStyleBlock(card, imageAssets.Select(a => (a.Kind, a.Label!))),
            templates,
            contextBlock);
    }

    private static string? BuildStyleBlock(string brandName, BrandStyleCard? card)
    {
        if (card is null)
        {
            return $"Brand: {brandName}.";
        }

        var sb = new StringBuilder();
        sb.Append("Brand: ").Append(brandName).AppendLine(".");
        Append(sb, "Brand voice", card.Voice);
        Append(sb, "Audience", card.Audience);
        Append(sb, "Tagline", card.Tagline);

        if (card.Colors is { Count: > 0 })
        {
            sb.Append("Brand colours: ")
              .AppendJoin(", ", card.Colors.Select(c => $"{c.Role} {c.Hex}"))
              .AppendLine(".");
        }

        if (card.BannedPhrases is { Count: > 0 })
        {
            sb.Append("Never use these phrases: ")
              .AppendJoin("; ", card.BannedPhrases)
              .AppendLine(".");
        }

        return sb.ToString().TrimEnd();
    }

    private static string? BuildImageStyleBlock(
        BrandStyleCard? card, IEnumerable<(string Kind, string Label)> assets)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(card?.ImageStyle))
        {
            sb.Append("Style: ").Append(card.ImageStyle.Trim()).AppendLine();
        }

        if (card?.Colors is { Count: > 0 })
        {
            sb.Append("Brand palette: ")
              .AppendJoin(", ", card.Colors.Select(c => $"{c.Role} {c.Hex}"))
              .AppendLine(".");
        }

        var backgrounds = assets.Where(a => a.Kind == "background").Select(a => a.Label).ToList();
        if (backgrounds.Count > 0)
        {
            sb.Append("Preferred backgrounds: ").AppendJoin("; ", backgrounds).AppendLine(".");
        }

        var faces = assets.Where(a => a.Kind == "face").Select(a => a.Label).ToList();
        if (faces.Count > 0)
        {
            sb.Append("People who may appear: ").AppendJoin("; ", faces).AppendLine(".");
        }

        return sb.Length == 0 ? null : sb.ToString().TrimEnd();
    }

    private static string? BuildCampaignContextBlock(IReadOnlyList<CampaignLink>? links)
    {
        if (links is not { Count: > 0 })
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Campaign context links (real destinations to reference and link to):");
        foreach (var link in links)
        {
            sb.Append("- ").Append(link.Label).Append(": ").Append(link.Url);
            if (!string.IsNullOrWhiteSpace(link.Note))
            {
                sb.Append(" — ").Append(link.Note);
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static void Append(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.Append(label).Append(": ").AppendLine(value.Trim());
        }
    }
}
