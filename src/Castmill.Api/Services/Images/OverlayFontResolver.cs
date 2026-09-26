using System.Collections.Concurrent;
using Castmill.Core.Resources;
using SkiaSharp;

namespace Castmill.Api.Services.Images;

/// <summary>
/// Maps an ADR-082 text layer's (family, weight) to one of the bundled static OFL faces in
/// Assets/Fonts. The UI RCL ships the same files, so both sides pick the same face and the
/// same synthetic-bold decision.
/// </summary>
internal sealed class OverlayFontResolver(string fontsDirectory)
{
    public static readonly OverlayFontResolver Shared =
        new(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts"));

    /// <summary>A requested weight this far above the chosen file's weight is emboldened synthetically.</summary>
    internal const int SyntheticBoldThreshold = 100;

    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<(int Weight, string File)>> Faces =
        new Dictionary<string, IReadOnlyList<(int Weight, string File)>>(StringComparer.Ordinal)
        {
            ["Barlow Condensed"] =
            [
                (400, "BarlowCondensed-Regular.ttf"),
                (500, "BarlowCondensed-Medium.ttf"),
                (600, "BarlowCondensed-SemiBold.ttf"),
                (700, "BarlowCondensed-Bold.ttf"),
                (800, "BarlowCondensed-ExtraBold.ttf"),
            ],
            ["Barlow"] =
            [
                (400, "Barlow-Regular.ttf"),
                (500, "Barlow-Medium.ttf"),
                (600, "Barlow-SemiBold.ttf"),
                (700, "Barlow-Bold.ttf"),
                (800, "Barlow-ExtraBold.ttf"),
            ],
            ["Anton"] = [(400, "Anton-Regular.ttf")],
            ["DM Serif Display"] = [(400, "DMSerifDisplay-Regular.ttf")],
            ["IBM Plex Mono"] =
            [
                (400, "IBMPlexMono-Regular.ttf"),
                (500, "IBMPlexMono-Medium.ttf"),
                (600, "IBMPlexMono-SemiBold.ttf"),
                (700, "IBMPlexMono-Bold.ttf"),
            ],
        };

    private readonly ConcurrentDictionary<string, SKTypeface?> _loaded = new(StringComparer.Ordinal);

    /// <summary>
    /// The bundled face the browser would pick for the same @font-face set (CSS Fonts 4 §5.2
    /// weight matching), so the preview and the composite draw the same file. For whole
    /// hundreds on these sets it is simply the nearest weight.
    /// </summary>
    internal static (string File, int FileWeight, bool Embolden) Select(string? family, int weight)
    {
        var faces = Faces[family is not null && Faces.ContainsKey(family) ? family : OverlayFonts.Default];
        var lighter = faces.Where(f => f.Weight < weight).OrderByDescending(f => f.Weight);
        var heavier = faces.Where(f => f.Weight >= weight).OrderBy(f => f.Weight);
        var order = weight switch
        {
            < 400 => faces.Where(f => f.Weight <= weight).OrderByDescending(f => f.Weight)
                .Concat(faces.Where(f => f.Weight > weight).OrderBy(f => f.Weight)),
            <= 500 => heavier.Where(f => f.Weight <= 500)
                .Concat(lighter)
                .Concat(faces.Where(f => f.Weight > 500).OrderBy(f => f.Weight)),
            _ => heavier.Concat(lighter),
        };
        var best = order.First();
        return (best.File, best.Weight, weight - best.Weight >= SyntheticBoldThreshold);
    }

    /// <summary>The face for a layer, or null when the bundled file is missing or unreadable.</summary>
    public (SKTypeface Typeface, bool Embolden)? Resolve(string? family, int weight)
    {
        var (file, _, embolden) = Select(family, weight);
        var typeface = _loaded.GetOrAdd(file, name =>
        {
            var path = Path.Combine(fontsDirectory, name);
            return File.Exists(path) ? SKTypeface.FromFile(path) : null;
        });
        return typeface is null ? null : (typeface, embolden);
    }
}
