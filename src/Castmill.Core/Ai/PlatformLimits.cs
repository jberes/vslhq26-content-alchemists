namespace Castmill.Core.Ai;

/// <summary>
/// Hard per-platform character caps — the single source of truth shared by
/// server validators and (later) the composer's char meters in the client.
/// </summary>
public static class PlatformLimits
{
    public static IReadOnlyDictionary<string, int> MaxChars { get; } = new Dictionary<string, int>
    {
        ["x"] = 280,
        ["bluesky"] = 300,
        ["mastodon"] = 500,
        ["threads"] = 500,
        ["pinterest"] = 500,
        ["instagram"] = 2_200,
        ["tiktok"] = 2_200,
        ["linkedin"] = 3_000,
        ["youtube"] = 5_000,
        ["facebook"] = 63_206,
    };

    public static int CharacterCount(string text) => text.EnumerateRunes().Count();

    public static int OverBy(string platform, string text) =>
        MaxChars.TryGetValue(platform, out var limit)
            ? Math.Max(0, CharacterCount(text) - limit)
            : 0;
}
