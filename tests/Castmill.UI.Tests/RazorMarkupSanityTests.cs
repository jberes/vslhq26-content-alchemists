using System.Text.RegularExpressions;

namespace Castmill.UI.Tests;

/// <summary>
/// Catches malformed markup that renders fine in bUnit and fatally in a browser.
///
/// The motivating bug: a Razor comment placed INSIDE a tag's attribute list. Razor does not
/// strip it there — it emits the entire comment as an attribute NAME. bUnit renders it without
/// complaint because AngleSharp tolerates invalid attribute names, so 130 component tests
/// passed while the real app died: the browser throws
/// <c>InvalidCharacterError: Failed to execute 'setAttribute'</c>, WebAssemblyRenderer tears
/// down the render tree, and the user gets a blank campaign under Blazor's global error bar.
///
/// No component test can cover this class of defect, which is exactly why it is a source scan.
/// </summary>
public sealed class RazorMarkupSanityTests
{
    [Fact]
    public void No_razor_comment_sits_inside_a_tags_attribute_list()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(UiRoot(), "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            // An opening tag, then @* before the tag has been closed by '>'.
            foreach (Match match in Regex.Matches(text, @"<[A-Za-z][^>]*?@\*", RegexOptions.Singleline))
            {
                var line = text[..match.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetFileName(file)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A Razor comment inside a tag's attribute list is emitted as an attribute NAME and "
            + "throws InvalidCharacterError in the browser, blanking the page. Move it above "
            + "the tag:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The same failure reached the browser through an attribute whose name came out malformed.
    /// Every attribute name a component writes must be a legal HTML name.
    /// </summary>
    [Fact]
    public void Every_attribute_name_in_markup_is_a_legal_html_name()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(UiRoot(), "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            foreach (Match tag in Regex.Matches(text, @"<[A-Za-z][A-Za-z0-9]*\s([^<>]*?)/?>", RegexOptions.Singleline))
            {
                // Attribute names are what precedes '=' at the start of a token. Razor
                // expressions (@onclick, @bind-*, @key) are legal here and skipped.
                foreach (Match attr in Regex.Matches(tag.Groups[1].Value, @"(^|\s)([^\s=/>""']+)\s*="))
                {
                    var name = attr.Groups[2].Value;
                    if (name.StartsWith('@') || Regex.IsMatch(name, @"^[A-Za-z_:][\w:.\-]*$"))
                    {
                        continue;
                    }

                    var line = text[..tag.Index].Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetFileName(file)}:{line} -> '{name}'");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These attribute names are not legal HTML; the browser rejects them with "
            + "InvalidCharacterError even though bUnit accepts them:\n  "
            + string.Join("\n  ", offenders));
    }

    private static string UiRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Castmill.UI")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "Castmill.UI");
    }
}
