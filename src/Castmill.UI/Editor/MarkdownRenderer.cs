using System.Text.RegularExpressions;
using Markdig;
using Microsoft.AspNetCore.Components;

namespace Castmill.UI.Editor;

/// <summary>
/// Renders markdown for the read-only surfaces — brand guidelines, AI context, artifact
/// previews. Pure .NET via Markdig; the TipTap bundle is only for editing (Roadmap §2.5).
///
/// SECURITY: this output goes through <c>MarkupString</c>, so it is raw HTML in the DOM. The
/// input is not trusted — it is model output and user paste. Two defences:
///   1. Markdig is configured to DISABLE raw HTML passthrough, so any &lt;tag&gt; in the
///      source is emitted as text.
///   2. Pseudo-tags are unwrapped first. AI text routinely contains things like
///      &lt;colors&gt; or &lt;tone&gt; that look like markup but are content; without step 2
///      they would render as escaped noise, and relying on escaping alone to be safe is one
///      configuration change away from an XSS hole.
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>
    /// Markdig with raw HTML off. <c>DisableHtml</c> is the load-bearing call: it is what
    /// makes rendering model output into the DOM safe rather than hopeful.
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .Build();

    /// <summary>
    /// Matches an XML-ish tag. Used to strip pseudo-tags from AI text before rendering; the
    /// timeout is belt-and-braces against a pathological input (CA1802-style ReDoS caution).
    /// </summary>
    private static readonly Regex PseudoTag = new(
        @"</?[A-Za-z][A-Za-z0-9_-]*\s*/?>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Tags that are content in this product's vocabulary rather than markup. They are
    /// unwrapped — the tag disappears, the text inside stays — because that is what the model
    /// meant. Anything not on this list is left alone and ends up escaped as visible text,
    /// which is the honest outcome for markup we do not recognise.
    /// </summary>
    private static readonly string[] UnwrappedPseudoTags =
    [
        "colors", "color", "tone", "voice", "style", "audience", "angle",
        "brand", "guidelines", "context", "example", "examples", "notes",
    ];

    /// <summary>Renders markdown to HTML for <c>MarkupString</c>.</summary>
    public static MarkupString ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new MarkupString(string.Empty);
        }

        return new MarkupString(Markdig.Markdown.ToHtml(Unwrap(markdown), Pipeline));
    }

    /// <summary>Plain text, for summaries and titles. No HTML anywhere near the DOM.</summary>
    public static string ToPlainText(string? markdown) =>
        string.IsNullOrWhiteSpace(markdown)
            ? string.Empty
            : Markdig.Markdown.ToPlainText(Unwrap(markdown), Pipeline).Trim();

    /// <summary>
    /// First sentence or so, for the "what changed" line on review cards. Deliberately
    /// derived from plain text, never from HTML.
    /// </summary>
    public static string Summarize(string? markdown, int maxLength = 140)
    {
        var text = ToPlainText(markdown).ReplaceLineEndings(" ").Trim();
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', Math.Min(maxLength, text.Length - 1));
        return string.Concat(text.AsSpan(0, cut > 40 ? cut : maxLength), "…");
    }

    /// <summary>Removes the known pseudo-tags, keeping their inner text.</summary>
    private static string Unwrap(string markdown)
    {
        try
        {
            return PseudoTag.Replace(markdown, match =>
            {
                var name = match.Value.Trim('<', '>', '/').Trim();
                return UnwrappedPseudoTags.Contains(name, StringComparer.OrdinalIgnoreCase)
                    ? string.Empty
                    : match.Value;
            });
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input: render it as-is. Markdig still has HTML disabled, so the
            // worst case is ugly, not unsafe.
            return markdown;
        }
    }
}
