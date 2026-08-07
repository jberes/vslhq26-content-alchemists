using System.Text.Json;
using System.Text.Json.Nodes;
using Castmill.Api.Services.Ai;
using Castmill.Core.Ai;

namespace Castmill.Api.Tests;

/// <summary>
/// Clip in/out points are computed from the transcript, never taken from numbers the model
/// wrote. The model is good at naming which moment it means and bad at arithmetic on
/// timestamps, so it nominates segments and this code does the timing — which is also what
/// makes the boundaries testable at all.
/// </summary>
public sealed class ClipBoundaryTests
{
    private static readonly TranscriptContent Transcript = new("test", [
        new TranscriptSegment("S1", 0, 5, null, "We launched the new product."),
        new TranscriptSegment("S2", 5, 12, null, "It cut deployment time in half."),
        new TranscriptSegment("S3", 12, 20, null, "Customers love the new dashboard."),
        new TranscriptSegment("S4", 20, 48, null, "The team shipped it in six weeks."),
    ]);

    private static JsonElement Apply(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ClipBoundaries.Apply(doc.RootElement.Clone(), Transcript);
    }

    private static JsonElement FirstClip(JsonElement result) =>
        result.GetProperty("clips").EnumerateArray().First();

    [Fact]
    public void Timings_come_from_the_named_segments_with_a_lead_in_and_a_tail()
    {
        var clip = FirstClip(Apply("""
            {"title":"Clips","clips":[{"startSegmentId":"S2","endSegmentId":"S3","hook":"h"}],"citations":["S2"]}
            """));

        // S2 starts at 5s, S3 ends at 20s. Pulled back 0.12s so the first consonant is not
        // sheared off, held 0.3s so the sentence lands.
        Assert.Equal(4.88, clip.GetProperty("inSeconds").GetDouble(), 2);
        Assert.Equal(20.3, clip.GetProperty("outSeconds").GetDouble(), 2);
    }

    /// <summary>
    /// The point of the whole change: a model that invents timestamps cannot move the cut.
    /// </summary>
    [Fact]
    public void Timestamps_the_model_invented_are_overwritten_by_the_transcript()
    {
        var clip = FirstClip(Apply("""
            {"title":"Clips","clips":[
              {"startSegmentId":"S1","endSegmentId":"S2","inSeconds":900,"outSeconds":1200,"hook":"h"}
            ],"citations":["S1"]}
            """));

        Assert.Equal(0, clip.GetProperty("inSeconds").GetDouble(), 2);   // clamped at zero
        Assert.Equal(12.3, clip.GetProperty("outSeconds").GetDouble(), 2);
    }

    [Fact]
    public void The_lead_in_never_runs_before_the_start_of_the_source()
    {
        var clip = FirstClip(Apply("""
            {"title":"Clips","clips":[{"startSegmentId":"S1","endSegmentId":"S1","hook":"h"}],"citations":["S1"]}
            """));

        Assert.Equal(0, clip.GetProperty("inSeconds").GetDouble(), 2);
    }

    [Fact]
    public void The_tail_never_runs_past_the_end_of_the_source()
    {
        var clip = FirstClip(Apply("""
            {"title":"Clips","clips":[{"startSegmentId":"S4","endSegmentId":"S4","hook":"h"}],"citations":["S4"]}
            """));

        // S4 ends at 48s, which is also the end of the transcript — no tail beyond it.
        Assert.Equal(48, clip.GetProperty("outSeconds").GetDouble(), 2);
    }

    [Fact]
    public void A_segment_id_array_works_as_well_as_a_start_end_pair()
    {
        var clip = FirstClip(Apply("""
            {"title":"Clips","clips":[{"segmentIds":["S3","S2"],"hook":"h"}],"citations":["S2"]}
            """));

        // Order in the array does not matter: the span is min-start to max-end.
        Assert.Equal(4.88, clip.GetProperty("inSeconds").GetDouble(), 2);
        Assert.Equal(20.3, clip.GetProperty("outSeconds").GetDouble(), 2);
    }

    /// <summary>An older-shaped response still validates rather than disappearing.</summary>
    [Fact]
    public void A_clip_with_no_usable_segment_ids_keeps_the_timings_it_came_with()
    {
        var clip = FirstClip(Apply("""
            {"title":"Clips","clips":[{"inSeconds":3,"outSeconds":19,"hook":"h"}],"citations":["S1"]}
            """));

        Assert.Equal(3, clip.GetProperty("inSeconds").GetDouble(), 2);
        Assert.Equal(19, clip.GetProperty("outSeconds").GetDouble(), 2);
    }

    /// <summary>
    /// A clip naming segments that do not exist, with nothing to fall back on, is dropped
    /// rather than failing the whole artifact — the same partial-failure rule the fan-out
    /// uses. The clips either side of it survive.
    /// </summary>
    [Fact]
    public void A_clip_naming_unknown_segments_is_dropped_and_the_rest_survive()
    {
        var result = Apply("""
            {"title":"Clips","clips":[
              {"startSegmentId":"S1","endSegmentId":"S2","hook":"good"},
              {"startSegmentId":"S99","endSegmentId":"S99","hook":"bogus"},
              {"startSegmentId":"S3","endSegmentId":"S4","hook":"also good"}
            ],"citations":["S1"]}
            """);

        var hooks = result.GetProperty("clips").EnumerateArray()
            .Select(c => c.GetProperty("hook").GetString())
            .ToList();

        Assert.Equal(["good", "also good"], hooks);
    }

    [Fact]
    public void A_payload_without_clips_passes_through_untouched()
    {
        var result = Apply("""{"title":"Nothing here","citations":["S1"]}""");
        Assert.Equal("Nothing here", result.GetProperty("title").GetString());
    }

    // ---- composite score ---------------------------------------------------------

    private static int Score(string clipJson) =>
        ClipBoundaries.CompositeScore((JsonObject)JsonNode.Parse(clipJson)!);

    [Fact]
    public void A_strong_well_sized_clip_outranks_a_weak_one()
    {
        var strong = Score("""
            {"inSeconds":0,"outSeconds":30,"scores":{"hook":9,"selfContained":9,"payoff":9,"emotion":8}}
            """);
        var weak = Score("""
            {"inSeconds":0,"outSeconds":30,"scores":{"hook":2,"selfContained":3,"payoff":2,"emotion":2}}
            """);

        Assert.True(strong > weak, $"strong {strong} should outrank weak {weak}");
        Assert.InRange(strong, 80, 100);
        Assert.InRange(weak, 0, 40);
    }

    /// <summary>
    /// Length is a multiplier, not a filter: an unusable-as-is length ranks lower but is
    /// still offered, because it is a real moment a human can trim.
    /// </summary>
    [Fact]
    public void An_identical_clip_outside_the_short_form_window_ranks_lower_but_not_zero()
    {
        var sweetSpot = Score("""
            {"inSeconds":0,"outSeconds":30,"scores":{"hook":9,"selfContained":9,"payoff":9,"emotion":9}}
            """);
        var tooLong = Score("""
            {"inSeconds":0,"outSeconds":300,"scores":{"hook":9,"selfContained":9,"payoff":9,"emotion":9}}
            """);

        Assert.True(tooLong < sweetSpot, $"{tooLong} should rank below {sweetSpot}");
        Assert.True(tooLong > 0, "an over-long clip is still a usable moment, not a zero");
    }

    [Fact]
    public void A_clip_the_model_did_not_score_lands_mid_scale_rather_than_at_zero()
    {
        // An unscored clip is unknown, not bad — ranking it last would bury moments the
        // model simply did not comment on.
        var unscored = Score("""{"inSeconds":0,"outSeconds":30}""");
        Assert.InRange(unscored, 40, 60);
    }
}
