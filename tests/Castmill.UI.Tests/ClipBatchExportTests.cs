using Bunit;
using Castmill.UI.Editor;
using Castmill.UI.Platform;

namespace Castmill.UI.Tests;

/// <summary>
/// Turning a long video into a set of Shorts used to be one click per clip, each waiting
/// on the last. These tests pin the batch: one press exports every suggestion in order,
/// carrying each clip's publishing copy and an ordinal file name, and a clip outside the
/// short-form window is flagged on the row rather than at upload time.
/// </summary>
public sealed class ClipBatchExportTests : CastmillUiTestContext
{
    private const string ThreeClips = """
        {"title":"Clips","clips":[
          {"inSeconds":10,"outSeconds":45,"hook":"Deploy time halved","clipTitle":"Deploy time, halved",
           "description":"How the team cut the pipeline down.","hashtags":["devops","shipping"],"platformFit":["shorts","tiktok"]},
          {"inSeconds":60,"outSeconds":95,"hook":"The rollback story","clipTitle":"The rollback that saved us",
           "description":"What went wrong at 2am.","hashtags":["sre"],"platformFit":["shorts"]},
          {"inSeconds":120,"outSeconds":128,"hook":"Too short to post","clipTitle":"A stray thought",
           "description":"Eight seconds.","hashtags":[],"platformFit":["shorts"]}
        ],"citations":["s01"]}
        """;

    [Fact]
    public async Task Export_all_cuts_every_clip_in_order_with_its_publishing_copy()
    {
        Media.EnableLocalProcessing();

        var view = Render<ClipReview>(p => p.Add(c => c.ContentJson, ThreeClips));

        var exportAll = view.FindAll("button").First(b => b.TextContent.Contains("Export all", StringComparison.Ordinal));
        await exportAll.ClickAsync();

        await view.WaitForStateAsync(() => Media.Exports.Count == 3, TimeSpan.FromSeconds(5));

        // In suggestion order, with ordinal names so the folder sorts the way the list reads.
        Assert.Equal([10, 60, 120], Media.Exports.Select(e => e.StartSeconds));
        Assert.Equal("clip-01-vertical", Media.Exports[0].OutputName);
        Assert.Equal("clip-03-vertical", Media.Exports[2].OutputName);

        // Each export carries the copy its upload form needs.
        var first = Media.Exports[0].PublishCopy;
        Assert.NotNull(first);
        Assert.Equal("Deploy time, halved", first!.Title);
        Assert.Equal(["devops", "shipping"], first.Hashtags);
        Assert.Equal("Deploy time halved", first.Hook);

        // Vertical is the default, so the batch is Shorts-shaped without extra clicks.
        Assert.All(Media.Exports, e => Assert.True(e.CropVertical));
    }

    [Fact]
    public async Task A_clip_outside_the_short_form_window_is_flagged_on_its_row()
    {
        Media.EnableLocalProcessing();

        var view = Render<ClipReview>(p => p.Add(c => c.ContentJson, ThreeClips));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-clips__clip").Count == 3, TimeSpan.FromSeconds(5));

        var flagged = view.FindAll(".cm-clips__length--off");
        Assert.Single(flagged); // only the 8-second one
        Assert.Contains("outside the 15–60s window", flagged[0].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_local_engine_the_batch_button_is_disabled_with_the_reason()
    {
        // Web shell: capability off. Nothing renders as a live control (G3).
        var view = Render<ClipReview>(p => p.Add(c => c.ContentJson, ThreeClips));

        var exportAll = view.FindAll("button").First(b => b.TextContent.Contains("Export all", StringComparison.Ordinal));
        Assert.True(exportAll.HasAttribute("disabled"));
        Assert.Contains("Not available in tests.", view.Markup, StringComparison.Ordinal);
        Assert.Empty(Media.Exports);
    }
}
