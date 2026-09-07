using System.Text.RegularExpressions;

namespace Castmill.UI.Tests;

/// <summary>
/// The brand editor's hex field must show a whole #RRGGBB.
///
/// A real defect, not a hypothetical: the field was sized <c>8.5ch</c> while every input is
/// <c>box-sizing: border-box</c>, so the padding and borders ate into that width and roughly
/// six of the seven characters fitted — each colour rendered clipped ("#2D2A9"). A width in
/// <c>ch</c> on a padded field has to add the chrome back, which is what this pins.
/// </summary>
public sealed class BrandColourFieldTests
{
    [Fact]
    public void The_hex_field_is_wide_enough_for_seven_characters_plus_its_own_padding()
    {
        var css = ReadWorkspaceFile("src/Castmill.UI/wwwroot/css/views.css");
        var rule = Regex.Match(css, @"\.cm-brand__color-hex\s*\{([^}]*)\}");
        Assert.True(rule.Success, ".cm-brand__color-hex is not defined");

        var width = Regex.Match(rule.Groups[1].Value, @"inline-size:\s*([^;]+);");
        Assert.True(width.Success, ".cm-brand__color-hex sets no inline-size");
        var value = width.Groups[1].Value.Trim();

        // Text room: at least the seven characters of #RRGGBB.
        var characters = Regex.Match(value, @"([\d.]+)ch");
        Assert.True(characters.Success, $"width '{value}' is not expressed in ch");
        Assert.True(
            double.Parse(characters.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 7,
            $"width '{value}' leaves fewer than seven characters of text");

        // Chrome: border-box means the padding and borders must be added back.
        Assert.Contains("var(--cm-space-3)", value, StringComparison.Ordinal);
        Assert.StartsWith("calc(", value, StringComparison.Ordinal);
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Castmill.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }
}
