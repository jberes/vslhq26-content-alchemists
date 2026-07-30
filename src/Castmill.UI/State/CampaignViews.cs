namespace Castmill.UI.State;

/// <summary>The four campaign views — header tabs, never rail items (ADR-F11).</summary>
public enum CampaignView
{
    MillFloor,
    Focus,
    ImageStudio,
    Seo,
}

/// <summary>
/// The single source of truth for campaign-view routing. Both the rail (which must preserve
/// the current view when switching campaign) and the header tabs read it, so a route can
/// never be spelled two ways.
/// </summary>
public static class CampaignViews
{
    public static readonly (CampaignView View, string Segment, string Label)[] All =
    [
        (CampaignView.MillFloor, "floor", "Mill Floor"),
        (CampaignView.Focus, "focus", "Focus mode"),
        (CampaignView.ImageStudio, "images", "Image studio"),
        (CampaignView.Seo, "seo", "SEO analysis"),
    ];

    public static string Segment(CampaignView view) =>
        All.First(v => v.View == view).Segment;

    public static string Label(CampaignView view) =>
        All.First(v => v.View == view).Label;

    public static string PathFor(Guid campaignId, CampaignView view) =>
        $"campaigns/{campaignId}/{Segment(view)}";

    /// <summary>
    /// Reads the view out of a relative path such as <c>campaigns/{id}/images</c>. Falls back
    /// to the Mill Floor, which is the campaign's landing view.
    /// </summary>
    public static CampaignView ViewFromPath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var segments = relativePath.Trim('/').Split('/');
        if (segments.Length < 3)
        {
            return CampaignView.MillFloor;
        }

        var last = segments[^1];
        foreach (var (view, segment, _) in All)
        {
            if (string.Equals(last, segment, StringComparison.OrdinalIgnoreCase))
            {
                return view;
            }
        }

        return CampaignView.MillFloor;
    }
}
