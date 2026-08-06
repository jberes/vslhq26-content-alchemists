namespace Castmill.UI.Design;

/// <summary>
/// Per-device UI state — pane sizes, zoom, theme choice (ADR-F06). Everything that must
/// roam between machines goes to the server instead.
///
/// Both shells use the same browser-storage implementation (a WebView has localStorage
/// too), so this is one of the few interfaces with a shared default rather than a
/// per-shell one. It exists as an interface so components and tests never touch JS.
/// </summary>
public interface IUiStateStore
{
    Task<string?> GetAsync(string key);

    Task SetAsync(string key, string value);

    /// <summary>The OS/browser colour-scheme preference, for first-run defaults only.</summary>
    Task<bool> PrefersDarkAsync();

    /// <summary>
    /// Writes the theme attributes onto the document root. A single call so the three
    /// attributes are set in one interop hop and the page can never be seen half-themed.
    /// </summary>
    Task ApplyThemeAsync(string family, string mode, string density);

    /// <summary>
    /// Writes (or removes, for null) the rail-collapse attribute on the document root:
    /// "icons" pins the rail closed, "labels" pins it open at the md tier, null restores
    /// the responsive default.
    /// </summary>
    Task ApplyRailAsync(string? state);
}
