using System.Text.Json;
using System.Text.RegularExpressions;

namespace Castmill.Api.Services.Ai;

/// <summary>
/// Stage 3 of the blog pipeline (ADR-071): the formatting contract the rendered article has to
/// meet. These are the faults that survive a content validator and only show up once the
/// Markdown is on a page — a table whose rows do not line up, an unclosed fence swallowing the
/// rest of the post, a link with no destination.
/// </summary>
internal static partial class BlogMarkdown
{
    [GeneratedRegex(@"^\s*\|.*\|\s*$")]
    private static partial Regex TableRow { get; }

    [GeneratedRegex(@"^\s*\|[\s:|-]+\|\s*$")]
    private static partial Regex TableSeparator { get; }

    [GeneratedRegex(@"\[[^\]]*\]\(\s*\)")]
    private static partial Regex EmptyLink { get; }

    [GeneratedRegex(@"^\s*#{2,6}\s*(.+\?)\s*$", RegexOptions.Multiline)]
    private static partial Regex QuestionHeading { get; }

    [GeneratedRegex(@"^\s*#\s+(\S.*?)\s*$", RegexOptions.Multiline)]
    private static partial Regex LeadingH1 { get; }

    /// <summary>
    /// The article's own top-level heading. The edit pass rewrites the body — heading and all —
    /// so the title captured from the DRAFT stops matching what the post actually says. This is
    /// what the piece calls itself once every stage has run.
    /// </summary>
    public static string? LeadingHeading(string markdown) =>
        string.IsNullOrWhiteSpace(markdown)
            ? null
            : LeadingH1.Match(markdown) is { Success: true } match ? match.Groups[1].Value.Trim() : null;

    /// <summary>
    /// The questions a published post already answers, read from its own question-shaped
    /// headings. Used to stop a later post in the same campaign repeating them: the campaign's
    /// SEO targets are one shared list, so without this every blog answers the same FAQ.
    /// Reads the stored envelope directly, so it works for posts written before this existed.
    /// </summary>
    public static IReadOnlyList<string> QuestionHeadings(string? contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson))
        {
            return [];
        }

        string markdown;
        try
        {
            using var document = JsonDocument.Parse(contentJson);
            var root = document.RootElement;
            var content = root.TryGetProperty("content", out var nested) ? nested : root;
            markdown = content.TryGetProperty("markdown", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;
        }
        catch (JsonException)
        {
            return [];
        }

        return markdown.Length == 0
            ? []
            : [.. QuestionHeading.Matches(markdown)
                .Select(match => match.Groups[1].Value.Trim())
                .Where(question => question.Length > 0)];
    }

    public static IReadOnlyList<string> Problems(string markdown)
    {
        var problems = new List<string>();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        if (CountFences(lines) % 2 != 0)
        {
            problems.Add("a code fence is not closed, so the rest of the article renders as code");
        }

        problems.AddRange(TableProblems(lines));

        if (EmptyLink.IsMatch(markdown))
        {
            problems.Add("a link has no destination");
        }

        if (!lines.Any(line => line.StartsWith("## ", StringComparison.Ordinal)))
        {
            problems.Add("no H2 sections, so the article has no scannable structure");
        }

        return problems;
    }

    private static int CountFences(string[] lines) =>
        lines.Count(line => line.TrimStart().StartsWith("```", StringComparison.Ordinal));

    /// <summary>
    /// A table needs a header row, a separator directly beneath it, and the same column count
    /// throughout. Anything else renders as literal pipes.
    /// </summary>
    private static IEnumerable<string> TableProblems(string[] lines)
    {
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence || !TableRow.IsMatch(lines[i]) || (i > 0 && TableRow.IsMatch(lines[i - 1])))
            {
                continue;
            }

            // lines[i] opens a table.
            if (i + 1 >= lines.Length || !TableSeparator.IsMatch(lines[i + 1]))
            {
                yield return $"a table near line {i + 1} has no separator row under its header";
                continue;
            }
            var columns = Columns(lines[i]);
            for (var row = i + 2; row < lines.Length && TableRow.IsMatch(lines[row]); row++)
            {
                if (Columns(lines[row]) != columns)
                {
                    yield return $"a table row near line {row + 1} has {Columns(lines[row])} columns, not {columns}";
                    break;
                }
            }
        }
    }

    private static int Columns(string row) =>
        row.Trim().Trim('|').Split('|').Length;
}

/// <summary>Rewrites one field of a generator payload, leaving every other property alone.</summary>
internal static class ArtifactContentJson
{
    public static System.Text.Json.JsonElement WithMarkdown(System.Text.Json.JsonElement content, string markdown)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(content.GetRawText())!.AsObject();
        node["markdown"] = markdown;
        using var document = System.Text.Json.JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    public static System.Text.Json.JsonElement WithTitle(System.Text.Json.JsonElement content, string title)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(content.GetRawText())!.AsObject();
        node["title"] = title;
        using var document = System.Text.Json.JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
