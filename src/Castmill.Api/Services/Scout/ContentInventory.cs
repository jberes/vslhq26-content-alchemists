using Castmill.Api.Data;
using Castmill.Core;
using Castmill.Core.Content;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Services.Scout;

/// <summary>What we have already drafted on a topic, whether or not it ever went live.</summary>
public sealed record InventoryHit(
    Guid ArtifactId, Guid CampaignId, string Kind, string Title, string Status, string Snippet);

public interface IContentInventory
{
    Task<IReadOnlyList<InventoryHit>> SearchAsync(string query, int take, CancellationToken ct);
}

/// <summary>
/// Searches the tenant's own artifacts.
///
/// This is the half of "have we covered this?" the knowledge gateway cannot answer: the
/// gateway knows what is PUBLISHED, and a post sitting in review is not on the site yet — but
/// proposing it again would still be a waste. The two sources answer different questions and
/// the Scout consults both.
///
/// Deliberately a LIKE over titles and payload text rather than a vector store: this is a few
/// hundred artifacts per tenant, and reaching for embeddings here would add a whole
/// index-freshness problem to solve a problem that does not exist yet.
/// </summary>
public sealed class ContentInventory(CastmillDbContext db) : IContentInventory
{
    private const int SnippetLength = 240;

    public async Task<IReadOnlyList<InventoryHit>> SearchAsync(string query, int take, CancellationToken ct)
    {
        var terms = Terms(query);
        if (terms.Count == 0)
        {
            return [];
        }

        // Title-first: a title match is a much stronger signal than the word appearing
        // somewhere in the body, and it keeps the expensive content scan to a shortlist.
        // Tenant-filtered by the global query filter (G1).
        var candidates = await db.Artifacts
            .Where(a => a.Kind != "transcript" && a.Kind != "image-prompts")
            .Where(a => terms.Any(t => EF.Functions.Like(a.Title, $"%{t}%"))
                        || terms.Any(t => EF.Functions.Like(a.ContentJson, $"%{t}%")))
            .OrderByDescending(a => a.UpdatedAt)
            .Take(Math.Clamp(take, 1, 25) * 4)
            .ToListAsync(ct);

        return [.. candidates
            .Select(a => new { Artifact = a, Score = Score(a, terms) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Artifact.UpdatedAt)
            .Take(Math.Clamp(take, 1, 25))
            .Select(x => new InventoryHit(
                x.Artifact.Id, x.Artifact.CampaignId, x.Artifact.Kind, x.Artifact.Title,
                x.Artifact.Status, Snippet(x.Artifact, terms)))];
    }

    /// <summary>
    /// Words worth matching on. Short words and the handful of joiners that appear in every
    /// document would match everything and rank nothing.
    /// </summary>
    private static List<string> Terms(string query) =>
        [.. (query ?? string.Empty)
            .Split([' ', ',', '.', '?', '!', ':', ';', '\n', '\t', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length >= 4 && !Stopwords.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .Take(8)];

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "about", "with", "from", "that", "this", "have", "what", "when", "where", "which",
        "your", "their", "them", "they", "there", "here", "into", "over", "than", "then",
        "will", "would", "could", "should", "been", "being", "does", "done", "just", "more",
        "most", "some", "such", "only", "also", "very", "much", "many", "each", "other",
    };

    private static int Score(Artifact artifact, List<string> terms)
    {
        var title = artifact.Title.ToLowerInvariant();
        var body = artifact.ContentJson.ToLowerInvariant();

        // A title hit is worth several body hits — "the post about X" beats "a post that
        // mentions X once in passing".
        return terms.Sum(t => (title.Contains(t, StringComparison.Ordinal) ? 5 : 0)
                              + (body.Contains(t, StringComparison.Ordinal) ? 1 : 0));
    }

    /// <summary>Text around the first matching term, so a hit is legible without opening it.</summary>
    private static string Snippet(Artifact artifact, List<string> terms)
    {
        var text = ArtifactMarkdown.Body(artifact.ContentJson)
                   ?? ArtifactMarkdown.MetaDescription(artifact.ContentJson)
                   ?? artifact.Title;

        var flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        var index = terms
            .Select(t => flat.IndexOf(t, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0)
            .DefaultIfEmpty(0)
            .Min();

        var start = Math.Max(0, index - 60);
        var length = Math.Min(SnippetLength, flat.Length - start);
        var snippet = flat.Substring(start, Math.Max(0, length)).Trim();
        return start > 0 ? "…" + snippet : snippet;
    }
}
