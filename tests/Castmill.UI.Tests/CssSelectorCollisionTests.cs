using System.Text.RegularExpressions;

namespace Castmill.UI.Tests;

/// <summary>
/// Two rules with the same single-class selector do not replace one another — they MERGE.
/// That is how the rail's brand lockup ended up stacked: <c>.cm-brand</c> was defined for the
/// Brands page with <c>flex-direction: column</c> and again, thousands of lines later, for the
/// lockup. The lockup's own rule won on the properties it declared and silently inherited
/// <c>column</c> on the one it didn't, putting the mark above the wordmark instead of beside it.
///
/// A duplicate base-class rule is almost always this mistake rather than a deliberate one, so
/// it fails the build. Deliberate layering (state and variant selectors like
/// <c>.cm-card:hover</c>, <c>.cm-card--wide</c>, media-query overrides) is untouched — only a
/// bare, repeated <c>.class { }</c> counts.
/// </summary>
public sealed class CssSelectorCollisionTests
{
    /// <summary>
    /// Redefining a class LATER in the same file to override specific properties is a real,
    /// deliberate pattern here — the F5 redesign layers over the F4 base that way, and those
    /// pairs describe the same component. They are pinned rather than banned, so the guard
    /// still fails on the next NEW duplicate, which is the one likely to be a mistake.
    /// </summary>
    private static readonly HashSet<string> KnownIntentionalOverrides = new(StringComparer.Ordinal)
    {
        "cm-front__secondary",
        "cm-chips",
        "cm-card--lane",
        "cm-focus",
        "cm-focus__outline",
        "cm-focus__manuscript",
    };

    [Fact]
    public void No_class_is_defined_twice_as_a_bare_selector()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(CssRoot(), "*.css", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);

            // A rule whose entire selector is one class: `.cm-thing {` at the start of a line.
            foreach (Match match in Regex.Matches(text, @"(?m)^\.([A-Za-z][\w-]*)\s*\{"))
            {
                var name = match.Groups[1].Value;
                var line = text[..match.Index].Count(c => c == '\n') + 1;

                if (KnownIntentionalOverrides.Contains(name))
                {
                    continue;
                }

                if (seen.TryGetValue(name, out var first))
                {
                    offenders.Add($"{Path.GetFileName(file)}: .{name} at line {first} and again at {line}");
                }
                else
                {
                    seen[name] = line;
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These classes are declared twice as bare selectors. The rules MERGE, so the second "
            + "one silently inherits whatever the first declared and it didn't — give one of "
            + "them its own name:\n  " + string.Join("\n  ", offenders));
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

    /// <summary>
    /// The same check ACROSS files, which is where it actually mattered. Every stylesheet is
    /// pulled into one cascade by castmill.css's @@import list, so a class defined in both
    /// layout.css and components.css merges exactly as if it were declared twice in one file —
    /// and the later import silently wins.
    ///
    /// That is how the Image Studio dialog ended up underneath the left rail: .cm-app__main
    /// was declared in layout.css AND in components.css, and only the components copy carried
    /// `z-index: 1`. That z-index made the element a stacking context, trapping the modal's
    /// `z-index: 70` inside it, so the rail painted over the dialog. Fixing the layout.css copy
    /// changed nothing, because it was never the rule that applied.
    /// </summary>
    [Fact]
    public void No_class_is_defined_as_a_bare_selector_in_two_different_files()
    {
        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(CssRoot(), "*.css", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (var cls in Regex.Matches(text, @"(?m)^\.([A-Za-z][\w-]*)\s*\{")
                         .Select(m => m.Groups[1].Value)
                         .Distinct(StringComparer.Ordinal))
            {
                if (!byName.TryGetValue(cls, out var files))
                {
                    byName[cls] = files = [];
                }
                files.Add(name);
            }
        }

        var offenders = byName
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => $".{kv.Key} in {string.Join(" and ", kv.Value)}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These classes are declared in more than one stylesheet. Every file lands in ONE "
            + "cascade, so the later @@import wins and edits to the other copy do nothing:\n  "
            + string.Join("\n  ", offenders));
    }


}
