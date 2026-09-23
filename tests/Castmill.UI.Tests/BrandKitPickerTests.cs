using System.Reflection;
using Castmill.Core.Resources;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// The kit picker's one load-bearing rule: what is selected and what the model is told must
/// never disagree.
///
/// The earlier design appended a phrase to the steering box on every click, so changing your
/// mind left the previous face still named in the prompt — the picture showed one selection
/// and the model was told two. Selections are now the source of truth and the sentence is
/// rebuilt from them, which is only correct if rebuilding also REMOVES what is no longer
/// selected and leaves the user's own words alone. That is what these check.
///
/// Selections are a multi-set: a card may carry a face AND a background AND a screenshot, up
/// to the API's limit of five. The per-kind replacement these once asserted is gone.
/// </summary>
public sealed class BrandKitPickerTests
{
    [Fact]
    public void Selecting_one_asset_per_kind_composes_a_single_sentence()
    {
        var view = new ImageStudioView();

        Toggle(view, Asset("face", "the host, short dark hair"));
        Toggle(view, Asset("background", "the Berlin studio wall"));

        var steering = Steering(view);
        Assert.Contains("featuring the host, short dark hair", steering, StringComparison.Ordinal);
        Assert.Contains("set against the Berlin studio wall", steering, StringComparison.Ordinal);

        // One sentence, not one per click.
        Assert.Equal(1, steering.Split("From the brand kit:").Length - 1);
    }

    /// <summary>
    /// Selections are a multi-set now: a second face ADDS, it does not replace. The rule this
    /// still protects is the original one — the sentence names exactly what is selected, no
    /// more and no less — which is why removing one below must drop it from the prompt.
    /// </summary>
    [Fact]
    public void A_second_asset_of_the_same_kind_is_added_not_swapped()
    {
        var view = new ImageStudioView();

        Toggle(view, Asset("face", "the host"));
        Toggle(view, Asset("face", "the guest"));

        var steering = Steering(view);
        Assert.Contains("the guest", steering, StringComparison.Ordinal);
        Assert.Contains("the host", steering, StringComparison.Ordinal);
        Assert.Equal(1, steering.Split("From the brand kit:").Length - 1);
    }

    [Fact]
    public void Selection_stops_at_the_api_limit_rather_than_silently_dropping_one()
    {
        var view = new ImageStudioView();
        for (var i = 0; i < 7; i++)
        {
            Toggle(view, Asset("other", $"reference {i}"));
        }

        var steering = Steering(view);
        // Five is what ImageSlotEndpoints accepts; the sixth and seventh must not appear.
        Assert.Contains("reference 4", steering, StringComparison.Ordinal);
        Assert.DoesNotContain("reference 5", steering, StringComparison.Ordinal);
        Assert.DoesNotContain("reference 6", steering, StringComparison.Ordinal);
    }

    [Fact]
    public void Clicking_the_selected_asset_again_clears_it_completely()
    {
        var view = new ImageStudioView();
        var face = Asset("face", "the host");

        Toggle(view, face);
        Toggle(view, face);

        Assert.DoesNotContain("From the brand kit", Steering(view), StringComparison.Ordinal);
        Assert.DoesNotContain("the host", Steering(view), StringComparison.Ordinal);
    }

    [Fact]
    public void The_users_own_steering_survives_every_change_of_selection()
    {
        var view = new ImageStudioView();
        SetSteering(view, "warmer light, shot from slightly below");

        var face = Asset("face", "the host");
        Toggle(view, face);
        Toggle(view, Asset("background", "the studio wall"));
        Toggle(view, face);   // remove the face again

        var steering = Steering(view);
        Assert.StartsWith("warmer light, shot from slightly below", steering, StringComparison.Ordinal);
        Assert.Contains("the studio wall", steering, StringComparison.Ordinal);
        Assert.DoesNotContain("the host", steering, StringComparison.Ordinal);
    }

    private static BrandAssetResponse Asset(string kind, string label) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), kind, label,
            $"{label}.png", "image/png", DateTimeOffset.UtcNow);

    private static void Toggle(ImageStudioView view, BrandAssetResponse asset) =>
        typeof(ImageStudioView)
            .GetMethod("TogglePick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(view, [asset]);

    private static string Steering(ImageStudioView view) =>
        typeof(ImageStudioView)
            .GetField("_steerNote", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(view) as string ?? string.Empty;

    private static void SetSteering(ImageStudioView view, string value) =>
        typeof(ImageStudioView)
            .GetField("_steerNote", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(view, value);
}
