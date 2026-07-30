using System.Globalization;
using System.Text.RegularExpressions;

namespace Castmill.UI.Tests;

/// <summary>
/// Makes F1's two check-in gates mechanical instead of eyeballed.
///
/// G6: every text pair must clear WCAG AA (4.5:1) in ALL FOUR family × mode combinations.
/// A palette tweak that quietly breaks contrast in, say, Blueprint dark is the exact
/// failure a style-guide review misses, because nobody clicks all four every time.
///
/// ADR-F09: feature CSS may reference only the semantic --cm-* layer — never a family's
/// raw --cmf-* value and never a colour literal. That is what makes a third family a new
/// file rather than a refactor, so it is worth a test rather than a code-review habit.
/// </summary>
public sealed class DesignTokenTests
{
    /// <summary>Pairs that carry text, as foreground/background token names.</summary>
    private static readonly (string Foreground, string Background)[] TextPairs =
    [
        ("--cmf-on-surface", "--cmf-surface"),
        ("--cmf-on-surface", "--cmf-surface-raised"),
        ("--cmf-on-surface", "--cmf-surface-sunken"),
        ("--cmf-on-surface-muted", "--cmf-surface"),
        ("--cmf-on-surface-muted", "--cmf-surface-raised"),
        ("--cmf-on-surface-subtle", "--cmf-surface"),
        ("--cmf-on-accent", "--cmf-accent-strong"),
        ("--cmf-on-inverse", "--cmf-surface-inverse"),
        ("--cmf-success", "--cmf-surface"),
        ("--cmf-warning", "--cmf-surface"),
        ("--cmf-danger", "--cmf-surface"),
    ];

    private static readonly string[] Families = ["warm-editorial", "industry-blueprint"];
    private static readonly string[] Modes = ["light", "dark"];

    [Fact]
    public void Every_text_pair_clears_WCAG_AA_in_all_four_family_and_mode_combinations()
    {
        var failures = new List<string>();

        foreach (var family in Families)
        {
            foreach (var mode in Modes)
            {
                var tokens = TokensFor(family, mode);

                foreach (var (fg, bg) in TextPairs)
                {
                    var foreground = Resolve(tokens, fg);
                    var background = Resolve(tokens, bg);
                    var ratio = ContrastRatio(foreground, background);

                    if (ratio < 4.5)
                    {
                        failures.Add(
                            $"{family}/{mode}: {fg} on {bg} is {ratio:0.00}:1 (needs 4.5:1)");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Every_family_defines_the_same_token_set()
    {
        // A family that forgets a token does not fail loudly — the semantic layer just
        // resolves to nothing and the affected element renders unstyled. Compare the sets.
        var reference = TokensFor("warm-editorial", "light").Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var mismatches = new List<string>();

        foreach (var family in Families)
        {
            foreach (var mode in Modes)
            {
                var keys = TokensFor(family, mode).Keys;
                var missing = reference.Except(keys, StringComparer.Ordinal).ToList();
                var extra = keys.Except(reference, StringComparer.Ordinal).ToList();

                if (missing.Count > 0)
                {
                    mismatches.Add($"{family}/{mode} is missing: {string.Join(", ", missing)}");
                }

                if (extra.Count > 0)
                {
                    mismatches.Add($"{family}/{mode} defines unknown: {string.Join(", ", extra)}");
                }
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void Feature_css_never_reaches_past_the_semantic_layer()
    {
        var cssRoot = Path.Combine(RepositoryRoot(), "src", "Castmill.UI", "wwwroot", "css");
        var tokenDir = Path.Combine(cssRoot, "tokens");
        var offences = new List<string>();

        foreach (var file in Directory.EnumerateFiles(cssRoot, "*.css", SearchOption.AllDirectories))
        {
            // The token sheets are where families and literals are allowed to exist.
            if (file.StartsWith(tokenDir, StringComparison.Ordinal))
            {
                continue;
            }

            var lines = StripComments(File.ReadAllText(file));
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                if (line.Contains("--cmf-", StringComparison.Ordinal))
                {
                    offences.Add($"{Path.GetFileName(file)}:{i + 1} reaches a family token: {lines[i].Trim()}");
                }

                // Hex literals. Data URIs (the paper-grain SVG) and #ids are not colours.
                if (Regex.IsMatch(line, @"(?<!&)#[0-9a-fA-F]{3,8}\b") && !line.Contains("url(", StringComparison.Ordinal))
                {
                    offences.Add($"{Path.GetFileName(file)}:{i + 1} hard-codes a colour: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offences.Count == 0, string.Join(Environment.NewLine, offences));
    }

    [Fact]
    public void Breakpoint_tokens_and_media_queries_agree()
    {
        // CSS cannot use custom properties in media queries, so the numbers are written
        // twice. This is the guard that stops the two copies drifting.
        var layout = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Castmill.UI", "wwwroot", "css", "layout.css"));

        var sm = TokenNumber(layout, "--cm-bp-sm");
        var md = TokenNumber(layout, "--cm-bp-md");

        // The rail rules use max-width one hundredth below the breakpoint.
        Assert.Contains($"max-width: {md - 0.02:0.##}px", layout, StringComparison.Ordinal);
        Assert.Contains($"max-width: {sm - 0.02:0.##}px", layout, StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------

    private static double TokenNumber(string css, string token)
    {
        var match = Regex.Match(css, Regex.Escape(token) + @":\s*(\d+(?:\.\d+)?)px");
        Assert.True(match.Success, $"{token} is not defined in layout.css");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Blanks out every /* … */ span while preserving line numbering, so the rule reads
    /// declarations only. Comments have to be stripped across lines, not per line: these
    /// sheets carry multi-line block comments that legitimately discuss --cmf-* tokens and
    /// colour values, and a per-line strip flagged those as violations.
    /// </summary>
    private static string[] StripComments(string text)
    {
        var output = new System.Text.StringBuilder(text.Length);
        var inComment = false;

        for (var i = 0; i < text.Length; i++)
        {
            if (!inComment && text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                inComment = true;
                i++;
                continue;
            }

            if (inComment && text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/')
            {
                inComment = false;
                i++;
                continue;
            }

            if (inComment)
            {
                // Keep newlines so reported line numbers still match the real file.
                if (text[i] == '\n')
                {
                    output.Append('\n');
                }

                continue;
            }

            output.Append(text[i]);
        }

        return output.ToString().Split('\n');
    }

    /// <summary>
    /// Reads a family sheet and returns the --cmf-* values that apply for the given mode.
    /// Light values come from the default block, then the mode block overrides them —
    /// the same cascade the browser applies.
    /// </summary>
    private static Dictionary<string, string> TokensFor(string family, string mode)
    {
        var path = Path.Combine(
            RepositoryRoot(), "src", "Castmill.UI", "wwwroot", "css", "tokens", $"family-{family}.css");
        var css = File.ReadAllText(path);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var block in Regex.Matches(css, @"(?<selectors>[^{}]+)\{(?<body>[^{}]*)\}"))
        {
            var match = (Match)block;
            var selectors = match.Groups["selectors"].Value;

            var isLightBlock = !selectors.Contains("data-cm-mode=\"dark\"", StringComparison.Ordinal);
            var isDarkBlock = selectors.Contains("data-cm-mode=\"dark\"", StringComparison.Ordinal);

            if (mode == "light" ? !isLightBlock : !isDarkBlock)
            {
                continue;
            }

            foreach (var declaration in Regex.Matches(
                         match.Groups["body"].Value, @"(--cmf-[a-z0-9-]+)\s*:\s*([^;]+);"))
            {
                var d = (Match)declaration;
                result[d.Groups[1].Value] = d.Groups[2].Value.Trim();
            }
        }

        Assert.NotEmpty(result);
        return result;
    }

    private static (double R, double G, double B) Resolve(Dictionary<string, string> tokens, string token)
    {
        Assert.True(tokens.TryGetValue(token, out var raw), $"{token} is not defined");
        return ParseColour(raw!);
    }

    private static (double R, double G, double B) ParseColour(string value)
    {
        value = value.Trim();

        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length == 3)
            {
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            }

            return (
                Convert.ToInt32(hex[..2], 16) / 255.0,
                Convert.ToInt32(hex[2..4], 16) / 255.0,
                Convert.ToInt32(hex[4..6], 16) / 255.0);
        }

        // rgb(r g b / a) — the alpha channel is deliberately ignored: these tokens are
        // only used where they sit on an opaque surface, and treating them as opaque is
        // the conservative reading for a contrast check.
        var numbers = Regex.Matches(value, @"[\d.]+")
            .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
            .ToList();

        Assert.True(numbers.Count >= 3, $"cannot parse colour '{value}'");
        return (numbers[0] / 255.0, numbers[1] / 255.0, numbers[2] / 255.0);
    }

    /// <summary>WCAG 2.1 relative luminance.</summary>
    private static double Luminance((double R, double G, double B) c)
    {
        static double Channel(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static double ContrastRatio((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var lighter = Math.Max(la, lb);
        var darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Castmill.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
