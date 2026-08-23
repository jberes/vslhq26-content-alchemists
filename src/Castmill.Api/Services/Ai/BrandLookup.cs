using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Castmill.Api.Services.Evidence;
using Castmill.Core.Resources;

namespace Castmill.Api.Services.Ai;

/// <summary>A drafted style card plus what the lookup could and could not determine.</summary>
public sealed record BrandLookupResult(
    string Name, BrandStyleCard StyleCard, string SourceUrl, IReadOnlyList<string> Notes);

public interface IBrandLookup
{
    /// <summary>
    /// Drafts a style card from a website, from pasted material, or from both. At least one
    /// must be present. When both are, the pasted material wins any disagreement — someone
    /// wrote it deliberately, whereas a home page is marketing copy we are reverse-engineering.
    /// </summary>
    Task<BrandLookupResult> LookupAsync(Guid userId, string? url, string? notes, CancellationToken ct);
}

/// <summary>
/// Drafts a brand from a public web page: fetch the page, pull out the signals a site
/// genuinely carries (title, description, palette, fonts), then let the model turn that into a
/// style card. The result is always a DRAFT for the user to edit — nothing is saved here.
///
/// The page is read server-side rather than by the model because no text provider in this app
/// has a browsing tool, and pretending otherwise would produce confident invention. What the
/// model gets is real page content; what it does is summarise and infer voice from it.
/// </summary>
public sealed partial class BrandLookup(
    IHttpClientFactory httpClientFactory,
    IChatProviderRegistry chatProviders) : IBrandLookup
{
    /// <summary>Enough of a page to characterise a brand; far short of a download.</summary>
    private const int MaxBytes = 512 * 1024;

    public async Task<BrandLookupResult> LookupAsync(
        Guid userId, string? url, string? notes, CancellationToken ct)
    {
        var hasUrl = !string.IsNullOrWhiteSpace(url);
        var hasNotes = !string.IsNullOrWhiteSpace(notes);
        if (!hasUrl && !hasNotes)
        {
            throw new BrandLookupException("Give a URL, some pasted context, or both.");
        }

        // Notes alone is a complete request: a marketing team's voice doc is better material
        // than a home page, and requiring a URL for it would be arbitrary.
        if (!hasUrl)
        {
            var fromNotes = await DraftAsync(userId, EmptyPage, notes, ct);
            return new BrandLookupResult(fromNotes.Name, fromNotes.StyleCard, "pasted context", []);
        }

        Uri target;
        try
        {
            target = await PublicUrlGuard.ValidateAsync(url!, ct);
        }
        catch (PublicUrlException ex)
        {
            throw new BrandLookupException(ex.Message);
        }
        var remarks = new List<string>();

        using var client = httpClientFactory.CreateClient("brandlookup");
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        // Some sites serve a stub to unknown agents; identifying ourselves honestly gets the
        // real page and is the polite thing to do.
        request.Headers.TryAddWithoutValidation("User-Agent", "Castmill-BrandLookup/1.0");
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new BrandLookupException($"{target.Host} returned {(int)response.StatusCode}.");
        }

        var html = await ReadCappedAsync(response, ct);
        var page = Extract(html, target);

        if (string.IsNullOrWhiteSpace(page.Text))
        {
            remarks.Add("The page had almost no readable text — it may be script-rendered.");
        }
        if (page.Colors.Count == 0)
        {
            remarks.Add("No palette was declared in the page's markup; colours are the model's reading.");
        }

        var card = await DraftAsync(userId, page, notes, ct);
        return new BrandLookupResult(
            string.IsNullOrWhiteSpace(card.Name) ? page.SiteName ?? target.Host : card.Name,
            card.StyleCard, target.ToString(), remarks);
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBytes];
        var total = 0;
        int read;
        while (total < MaxBytes
               && (read = await stream.ReadAsync(buffer.AsMemory(total, MaxBytes - total), ct)) > 0)
        {
            total += read;
        }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    internal sealed record PageSignals(
        string? Title, string? Description, string? SiteName, IReadOnlyList<string> Colors,
        IReadOnlyList<string> Fonts, string Text, string Url);

    /// <summary>
    /// Pulls the signals a site actually declares. Deliberately regex over a DOM parser: we
    /// want a handful of head tags and a rough text body, not a correct tree, and adding an
    /// HTML parser dependency for that is not a trade worth making.
    /// </summary>
    internal static PageSignals Extract(string html, Uri url)
    {
        var title = Meta(html, "og:site_name") ?? TitleTag().Match(html).Groups[1].Value.Trim();
        var description = Meta(html, "og:description") ?? Meta(html, "description");

        var colors = HexColor().Matches(StyleBlocks(html))
            .Select(m => "#" + m.Groups[1].Value.ToLowerInvariant())
            .Select(Normalise)
            .Where(c => c is not null)
            .GroupBy(c => c!, StringComparer.OrdinalIgnoreCase)
            // Frequency, not order: the colours a site repeats are its palette; the one-offs
            // are borders and shadows.
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(6)
            .ToList();

        if (Meta(html, "theme-color") is { Length: > 0 } theme && Normalise(theme) is { } themed)
        {
            colors.Remove(themed);
            colors.Insert(0, themed);
        }

        var fonts = FontFamily().Matches(StyleBlocks(html))
            // Only the first family in a stack is the brand's choice; the rest are fallbacks.
            .Select(m => m.Groups[1].Value.Split(',')[0].Trim().Trim('"', '\'', ' '))
            .Where(f => f.Length is > 1 and < 40)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        var text = Tags().Replace(ScriptsAndStyles().Replace(html, " "), " ");
        text = WebUtility.HtmlDecode(Whitespace().Replace(text, " ")).Trim();
        if (text.Length > 6000)
        {
            text = text[..6000];
        }

        return new PageSignals(
            string.IsNullOrWhiteSpace(title) ? null : title,
            description, title, colors!, fonts, text, url.ToString());

        static string? Normalise(string raw)
        {
            var hex = raw.TrimStart('#');
            if (hex.Length == 3)
            {
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            }
            return hex.Length == 6 ? "#" + hex.ToUpperInvariant() : null;
        }
    }

    private static string? Meta(string html, string name)
    {
        var match = Regex.Match(
            html,
            $"""<meta[^>]+(?:name|property)\s*=\s*["']{Regex.Escape(name)}["'][^>]*>""",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }
        var content = Regex.Match(match.Value, """content\s*=\s*["']([^"']*)["']""", RegexOptions.IgnoreCase);
        var value = WebUtility.HtmlDecode(content.Groups[1].Value).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string StyleBlocks(string html)
    {
        var styles = string.Concat(StyleTag().Matches(html).Select(m => m.Value));
        var inline = string.Concat(InlineStyle().Matches(html).Select(m => m.Value));
        return styles + inline;
    }

    private sealed record Draft(string Name, BrandStyleCard StyleCard);

    /// <summary>Stands in for a page when only pasted material was supplied.</summary>
    private static readonly PageSignals EmptyPage =
        new(null, null, null, [], [], string.Empty, "(no website supplied)");

    private async Task<Draft> DraftAsync(
        Guid userId, PageSignals page, string? notes, CancellationToken ct)
    {
        var prompt = $$"""
            You are drafting a brand style card for a marketing tool, from one page of the
            brand's own website. Use ONLY what the page supports. Where the page says nothing,
            leave the field out rather than inventing it.

            Reply with JSON only, no prose, matching exactly:
            {
              "name": string,
              "voice": string,        // how this brand writes: register, stance, sentence shape
              "audience": string,     // who it is talking to
              "tagline": string,
              "colors": [ { "role": string, "hex": "#RRGGBB" } ],
              "headingFont": string,
              "bodyFont": string,
              "imageStyle": string,   // what its imagery looks like, as a prompt fragment
              "bannedPhrases": [ string ]   // clichés this brand's voice would not use
            }

            Rules:
            - Every hex MUST be exactly #RRGGBB. Drop any colour you are unsure of.
            - "voice" should be usable as an instruction to a writer, not a description of it.
            - "bannedPhrases" is for generic marketing filler ("game-changing", "seamless"),
              only where the page's own voice clearly avoids that register.

            URL: {{page.Url}}
            Site name: {{page.SiteName ?? "(none)"}}
            Title: {{page.Title ?? "(none)"}}
            Description: {{page.Description ?? "(none)"}}
            Declared colours: {{(page.Colors.Count > 0 ? string.Join(", ", page.Colors) : "(none found)")}}
            Declared fonts: {{(page.Fonts.Count > 0 ? string.Join(", ", page.Fonts) : "(none found)")}}

            Page text:
            {{(string.IsNullOrWhiteSpace(page.Text) ? "(no website supplied)" : page.Text)}}
            {{PastedBlock(notes)}}
            """;

        var client = await chatProviders.ResolveAsync(userId, "chat", ct);
        var response = await client.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, prompt)],
            cancellationToken: ct);

        // ParseModelJson already tolerates fenced/prefixed output and hands back an element.
        var root = AiOrchestrator.ParseModelJson(response.Text);

        return new Draft(
            Str(root, "name") ?? page.SiteName ?? "New brand",
            new BrandStyleCard(
                Voice: Str(root, "voice"),
                Audience: Str(root, "audience"),
                Tagline: Str(root, "tagline"),
                Colors: Colors(root),
                HeadingFont: Str(root, "headingFont"),
                BodyFont: Str(root, "bodyFont"),
                ImageStyle: Str(root, "imageStyle"),
                BannedPhrases: Strings(root, "bannedPhrases")));

        static string? Str(JsonElement root, string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()
                : null;

        static IReadOnlyList<string>? Strings(JsonElement root, string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? [.. v.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Take(50)]
                : null;

        // Anything not exactly #RRGGBB is dropped here rather than at the endpoint: the write
        // path rejects a bad hex with a 400, and one hallucinated colour must not cost the
        // user the whole lookup.
        static IReadOnlyList<BrandColor>? Colors(JsonElement root)
        {
            if (!root.TryGetProperty("colors", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var colors = array.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object)
                .Select(e => new BrandColor(
                    e.TryGetProperty("role", out var r) ? r.GetString() ?? "colour" : "colour",
                    e.TryGetProperty("hex", out var h) ? h.GetString() ?? string.Empty : string.Empty))
                .Where(c => HexColorStrict().IsMatch(c.Hex))
                .Take(12)
                .ToList();
            return colors.Count > 0 ? colors : null;
        }
    }

    /// <summary>
    /// Pasted material outranks the website, and the prompt has to say so explicitly —
    /// otherwise the model averages a deliberate voice doc together with home-page copy.
    /// Capped because a pasted brand guide can be enormous.
    /// </summary>
    private static string PastedBlock(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return string.Empty;
        }

        var trimmed = notes.Trim();
        if (trimmed.Length > 40_000)
        {
            trimmed = trimmed[..40_000];
        }

        return $"""

            Material the brand's own team supplied. This is AUTHORITATIVE: where it disagrees
            with the website, follow this. Where it states a voice, palette or image direction
            outright, use it verbatim rather than paraphrasing.

            {trimmed}
            """;
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<style[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleTag();

    [GeneratedRegex(@"style\s*=\s*""[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex InlineStyle();

    [GeneratedRegex(@"#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})\b")]
    private static partial Regex HexColor();

    // The quote must NOT be excluded here: font stacks are routinely quoted
    // (font-family: "IBM Plex Sans", sans-serif), and stopping at the quote captured nothing.
    [GeneratedRegex(@"font-family\s*:\s*([^;}]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FontFamily();

    [GeneratedRegex(@"<(script|style|noscript)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptsAndStyles();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColorStrict();
}

/// <summary>A lookup failure the user can act on — reported as a 400, never a 500.</summary>
public sealed class BrandLookupException(string message) : Exception(message);
