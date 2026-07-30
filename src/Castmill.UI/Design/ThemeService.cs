using Microsoft.JSInterop;

namespace Castmill.UI.Design;

/// <summary>The two theme families that ship together (ADR-F09).</summary>
public enum ThemeFamily
{
    /// <summary>Warm, editorial, book-like. The default.</summary>
    WarmEditorial,

    /// <summary>Technical, drafting-table — the Mill Floor handoff's sheet.</summary>
    IndustryBlueprint,
}

public enum ThemeMode
{
    Light,
    Dark,
}

public enum ThemeDensity
{
    Comfortable,
    Compact,
}

/// <summary>
/// Owns the family × mode × density choice and applies it as attributes on the document
/// root, which is all switching costs — one class swap, no reload, no flash (E3.5).
///
/// The choice is per-device UI state, so it is persisted through
/// <see cref="IUiStateStore"/> rather than to the server (ADR-F06).
/// </summary>
public sealed class ThemeService(IUiStateStore store)
{
    private const string FamilyKey = "cm.theme.family";
    private const string ModeKey = "cm.theme.mode";
    private const string DensityKey = "cm.theme.density";

    private bool _initialized;

    public ThemeFamily Family { get; private set; } = ThemeFamily.WarmEditorial;

    public ThemeMode Mode { get; private set; } = ThemeMode.Light;

    public ThemeDensity Density { get; private set; } = ThemeDensity.Comfortable;

    /// <summary>Raised after any change so components can re-render — including the
    /// provenance overlay, which must re-measure when the theme changes (ADR-F09).</summary>
    public event Action? Changed;

    /// <summary>
    /// Restores the stored choice, falling back to the OS preference on first run. Safe to
    /// call more than once; only the first call reads storage.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        var storedFamily = await store.GetAsync(FamilyKey);
        var storedMode = await store.GetAsync(ModeKey);
        var storedDensity = await store.GetAsync(DensityKey);

        Family = Enum.TryParse<ThemeFamily>(storedFamily, out var family) ? family : ThemeFamily.WarmEditorial;
        Density = Enum.TryParse<ThemeDensity>(storedDensity, out var density) ? density : ThemeDensity.Comfortable;

        // First run has no stored mode: honour prefers-color-scheme rather than assuming.
        Mode = Enum.TryParse<ThemeMode>(storedMode, out var mode)
            ? mode
            : await store.PrefersDarkAsync() ? ThemeMode.Dark : ThemeMode.Light;

        await ApplyAsync();
    }

    public Task SetFamilyAsync(ThemeFamily family)
    {
        Family = family;
        return PersistAndApplyAsync(FamilyKey, family.ToString());
    }

    public Task SetModeAsync(ThemeMode mode)
    {
        Mode = mode;
        return PersistAndApplyAsync(ModeKey, mode.ToString());
    }

    public Task SetDensityAsync(ThemeDensity density)
    {
        Density = density;
        return PersistAndApplyAsync(DensityKey, density.ToString());
    }

    public Task ToggleModeAsync() =>
        SetModeAsync(Mode == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Light);

    public Task ToggleFamilyAsync() =>
        SetFamilyAsync(Family == ThemeFamily.WarmEditorial ? ThemeFamily.IndustryBlueprint : ThemeFamily.WarmEditorial);

    /// <summary>The attribute values the CSS selectors key on. Kept here so the token
    /// sheets and the service cannot disagree about spelling.</summary>
    public static string ToAttribute(ThemeFamily family) => family switch
    {
        ThemeFamily.WarmEditorial => "warm-editorial",
        ThemeFamily.IndustryBlueprint => "industry-blueprint",
        _ => "warm-editorial",
    };

    public static string ToAttribute(ThemeMode mode) => mode == ThemeMode.Dark ? "dark" : "light";

    public static string ToAttribute(ThemeDensity density) =>
        density == ThemeDensity.Compact ? "compact" : "comfortable";

    private async Task PersistAndApplyAsync(string key, string value)
    {
        await store.SetAsync(key, value);
        await ApplyAsync();
    }

    private async Task ApplyAsync()
    {
        await store.ApplyThemeAsync(ToAttribute(Family), ToAttribute(Mode), ToAttribute(Density));
        Changed?.Invoke();
    }
}
