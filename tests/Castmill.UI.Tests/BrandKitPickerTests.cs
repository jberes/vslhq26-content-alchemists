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
/// </summary>
public sealed class BrandKitPickerTests
{
    [Fact]
    public void Selecting_one_asset_per_kind_composes_a_single_sentence()
    {
        var view = new ImageStudioView();

        Toggle(view, "face", Asset("face", "the host, short dark hair"));
        Toggle(view, "background", Asset("background", "the Berlin studio wall"));

        var steering = Steering(view);
        Assert.Contains("featuring the host, short dark hair", steering, StringComparison.Ordinal);
        Assert.Contains("set against the Berlin studio wall", steering, StringComparison.Ordinal);

        // One sentence, not one per click.
        Assert.Equal(1, steering.Split("From the brand kit:").Length - 1);
    }

    [Fact]
    public void Choosing_a_different_face_replaces_the_first_rather_than_naming_both()
    {
        var view = new ImageStudioView();

        Toggle(view, "face", Asset("face", "the host"));
        Toggle(view, "face", Asset("face", "the guest"));

        var steering = Steering(view);
        Assert.Contains("the guest", steering, StringComparison.Ordinal);

        // The bug this exists for: the replaced face lingering in the prompt.
        Assert.DoesNotContain("the host", steering, StringComparison.Ordinal);
    }

    [Fact]
    public void Clicking_the_selected_asset_again_clears_it_completely()
    {
        var view = new ImageStudioView();
        var face = Asset("face", "the host");

        Toggle(view, "face", face);
        Toggle(view, "face", face);

        Assert.DoesNotContain("From the brand kit", Steering(view), StringComparison.Ordinal);
        Assert.DoesNotContain("the host", Steering(view), StringComparison.Ordinal);
    }

    [Fact]
    public void The_users_own_steering_survives_every_change_of_selection()
    {
        var view = new ImageStudioView();
        SetSteering(view, "warmer light, shot from slightly below");

        var face = Asset("face", "the host");
        Toggle(view, "face", face);
        Toggle(view, "background", Asset("background", "the studio wall"));
        Toggle(view, "face", face);   // clear the face again

        var steering = Steering(view);
        Assert.StartsWith("warmer light, shot from slightly below", steering, StringComparison.Ordinal);
        Assert.Contains("the studio wall", steering, StringComparison.Ordinal);
        Assert.DoesNotContain("the host", steering, StringComparison.Ordinal);
    }

    private static BrandAssetResponse Asset(string kind, string label) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), kind, label,
            $"{label}.png", "image/png", DateTimeOffset.UtcNow);

    private static void Toggle(ImageStudioView view, string kind, BrandAssetResponse asset) =>
        typeof(ImageStudioView)
            .GetMethod("TogglePick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(view, [kind, asset]);

    private static string Steering(ImageStudioView view) =>
        typeof(ImageStudioView)
            .GetField("_steerNote", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(view) as string ?? string.Empty;

    private static void SetSteering(ImageStudioView view, string value) =>
        typeof(ImageStudioView)
            .GetField("_steerNote", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(view, value);
}
