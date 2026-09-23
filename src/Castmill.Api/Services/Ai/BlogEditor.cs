using System.Reflection;
using System.Text.Json;
using System.Text;
using Castmill.Core.Ai;

namespace Castmill.Api.Services.Ai;

/// <summary>
/// Stage 2 of the blog pipeline (ADR-071): a separate model call that receives the draft, the
/// original approved evidence and the editing brief, and returns a revised article.
///
/// It is a rewrite, not a review. The previous pipeline ended in an audit that only listed
/// unsupported claims as warnings, so a draft's repetition, keyword-shaped headings and
/// transcript-annotation voice all shipped. The editor needs the evidence because it must be
/// able to correct a claim rather than guess at it.
/// </summary>
internal static class BlogEditor
{
    /// <summary>The authored brief, compiled in from docs/blog-editor.md.</summary>
    public static string Brief { get; } = LoadBrief();

    private static string LoadBrief()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Castmill.Api.BlogEditor.md")
            ?? throw new InvalidOperationException(
                "The blog editing brief is missing from the assembly. It is embedded from docs/blog-editor.md.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    /// <summary>
    /// The draft is handed over as an article, not as JSON: the brief tells the editor to
    /// return Markdown, and asking for a JSON envelope in the same breath produced escaped
    /// prose. Citations are carried over from the draft — the editor may not add claims, so it
    /// has no business adding sources.
    /// </summary>
    public static string BuildPrompt(string title, string draftMarkdown, int minimumWords)
    {
        var text = new StringBuilder();
        text.AppendLine(Brief);
        text.AppendLine();
        text.AppendLine("LENGTH");
        text.Append("The finished article must be at least ").Append(minimumWords)
            .AppendLine(" words. Consolidating duplication is required, but where that removes");
        text.AppendLine("substance, deepen the surviving sections from the approved evidence rather than");
        text.AppendLine("padding. Never pad to reach a number.");
        text.AppendLine();
        text.AppendLine("DRAFT TO REVISE");
        text.Append("Title: ").AppendLine(title);
        text.AppendLine();
        text.AppendLine(draftMarkdown);
        return text.ToString();
    }

    /// <summary>
    /// Splits the reply into the article and any editorial notes. The brief asks for the
    /// article followed by an optional "Editorial notes" section; a model that returns a fenced
    /// block or a lead-in sentence is still read correctly rather than publishing its wrapper.
    /// </summary>
    public static (string Markdown, IReadOnlyList<string> Notes) Parse(string reply) =>
        StripNotes(Unwrap(Unfence(reply.Trim())));

    /// <summary>
    /// The edit brief asks for Markdown, deliberately without a JSON envelope. A model that
    /// answers with one anyway used to be persisted verbatim: the whole JSON document became
    /// the value of the artifact's <c>markdown</c> field, and the producer opened Focus to a
    /// wall of escaped prose. Validation cannot catch it — a JSON document IS a non-empty
    /// string. So it is caught here, at the one place an edit reply becomes an article body:
    /// a recognisable prose field is unwrapped, and an envelope with none returns empty so the
    /// caller's floor keeps the unedited draft rather than publishing the wrapper.
    /// </summary>
    public static string Unwrap(string reply)
    {
        var text = reply.Trim();
        if (text.Length == 0 || text[0] != '{')
        {
            return reply;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return reply;
            }
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Prose that merely starts with a brace is not an envelope.
            return reply;
        }

        var host = root.TryGetProperty("artifact", out var artifact) && artifact.ValueKind == JsonValueKind.Object
            ? artifact
            : root;

        foreach (var name in ProseFields)
        {
            if (host.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } prose)
            {
                return prose.Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>What models reach for when they wrap an article they were asked to return bare.</summary>
    private static readonly string[] ProseFields = ["markdown", "article", "body", "content", "text"];

    /// <summary>
    /// The guard every blog body passes before it is persisted (ADR-077): a trailing section
    /// addressed to the editor — whatever the model called it — is cut out of the article and
    /// returned as notes. A reader must never see "the supplied evidence is a screen recording".
    /// </summary>
    public static (string Markdown, IReadOnlyList<string> Notes) StripNotes(string markdown)
    {
        var text = markdown.Trim();
        var index = FindNotesHeading(text);
        if (index < 0)
        {
            return (text, []);
        }

        var article = text[..index].Trim();
        var notes = text[index..]
            .Split('\n')
            .Skip(1)
            .Select(line => line.Trim().TrimStart('-', '*', '•').Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Take(20)
            .ToList();
        return (article, notes);
    }

    // A heading of any level, or a bold stand-alone line, naming the notes the brief asks for —
    // or any of the names models reach for instead.
    private static readonly System.Text.RegularExpressions.Regex NotesHeading = new(
        @"^\s*(?:#{1,6}\s*|\*\*)\s*(?:editorial notes?|editor['’]?s? notes?|notes? (?:to|for) the editor|publication blockers?|evidence gaps?|reviewer notes?|notes? for review)\s*:?\s*(?:\*\*)?\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static int FindNotesHeading(string text)
    {
        foreach (var line in EnumerateLines(text))
        {
            if (NotesHeading.IsMatch(line.Text.TrimEnd('\r')))
            {
                return line.Start;
            }
        }
        return -1;
    }

    private static IEnumerable<(string Text, int Start)> EnumerateLines(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0)
            {
                yield return (text[start..], start);
                yield break;
            }
            yield return (text[start..end], start);
            start = end + 1;
        }
    }

    /// <summary>A whole reply wrapped in one fence is the wrapper, not the article.</summary>
    private static string Unfence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }
        var firstNewline = text.IndexOf('\n', StringComparison.Ordinal);
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline >= 0 && lastFence > firstNewline ? text[(firstNewline + 1)..lastFence].Trim() : text;
    }

    public static int WordCount(string markdown) =>
        markdown.Split([' ', '\n', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
}
