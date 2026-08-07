using System.Text.RegularExpressions;

namespace Castmill.UI.Tests;

/// <summary>
/// Every <c>var(--cm-…)</c> a stylesheet references must actually be defined.
///
/// This exists because of a real failure, not a hypothetical one: the space scale is
/// 1,2,3,4,6,8,12 and a rule was written using <c>--cm-space-5</c>. CSS drops the ENTIRE
/// declaration when a custom property is undefined and has no fallback, so a panel shipped
/// with no padding at all and simply looked broken. Nothing caught it — the build was clean,
/// the tests were green, and the token-hygiene tests only check that colours come from the
/// semantic layer, not that the names resolve.
/// </summary>
public sealed class CssTokenReferenceTests
{
    private static readonly Regex Reference = new(@"var\(\s*(--cm-[a-z0-9-]+)", RegexOptions.Compiled);
    private static readonly Regex Definition = new(@"^\s*(--cm-[a-z0-9-]+)\s*:", RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public void Every_referenced_token_is_defined_somewhere_in_the_stylesheets()
    {
        var cssRoot = CssRoot();
        var files = Directory.GetFiles(cssRoot, "*.css", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        // Definitions can live anywhere (tokens/ defines them; a feature file may define a
        // local one), so the check is "resolvable", not "declared in tokens/".
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (Match match in Definition.Matches(File.ReadAllText(file)))
            {
                defined.Add(match.Groups[1].Value);
            }
        }

        var dangling = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Reference.Matches(text))
            {
                var name = match.Groups[1].Value;
                if (defined.Contains(name))
                {
                    continue;
                }

                // A reference WITH a fallback — var(--x, 1rem) — degrades gracefully, so it
                // is a style choice rather than a bug.
                var tail = text[match.Index..Math.Min(text.Length, match.Index + 120)];
                if (tail.Contains(',', StringComparison.Ordinal)
                    && tail.IndexOf(',', StringComparison.Ordinal) < tail.IndexOf(')', StringComparison.Ordinal))
                {
                    continue;
                }

                dangling.Add($"{Path.GetFileName(file)}: {name} (line {Line(text, match.Index)})");
            }
        }

        Assert.True(dangling.Count == 0,
            "These custom properties are referenced but never defined, so CSS silently drops "
            + "the whole declaration:\n  " + string.Join("\n  ", dangling.Distinct()));
    }

    /// <summary>The space scale has gaps on purpose; naming a missing step must not compile away.</summary>
    [Fact]
    public void The_space_scale_has_the_steps_the_stylesheets_actually_use()
    {
        var semantic = File.ReadAllText(Path.Combine(CssRoot(), "tokens", "semantic.css"));
        var steps = Definition.Matches(semantic)
            .Select(m => m.Groups[1].Value)
            .Where(n => n.StartsWith("--cm-space-", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        // Pinned so a future edit that removes a step fails here rather than in a browser.
        Assert.Contains("--cm-space-1", steps);
        Assert.Contains("--cm-space-6", steps);
        Assert.Contains("--cm-space-8", steps);
        Assert.DoesNotContain("--cm-space-5", steps);
    }

    private static string CssRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Castmill.UI")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "Castmill.UI", "wwwroot", "css");
    }

    private static int Line(string text, int index) =>
        text.AsSpan(0, index).Count('\n') + 1;
}
