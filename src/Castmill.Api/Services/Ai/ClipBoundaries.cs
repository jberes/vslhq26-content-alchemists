using System.Text.Json;
using System.Text.Json.Nodes;
using Castmill.Core.Ai;

namespace Castmill.Api.Services.Ai;

/// <summary>
/// Turns a clip suggestion into real in/out points.
///
/// The model is asked which SEGMENTS a clip spans, never for timestamps. Every write-up on
/// building this kind of pipeline lands on the same rule, and img.ly put it plainly: models
/// "can hallucinate timestamps or miscalculate offsets, but they're excellent at identifying
/// the right words." The transcript is the ground truth for timing, so the model nominates
/// and this code does the arithmetic.
///
/// Boundary quality is the difference between a clip that opens on the hook and one that
/// opens 1.5 seconds before it — the single thing reviewers most often criticise in this
/// category — so the snapping below is deliberate rather than incidental.
/// </summary>
internal static class ClipBoundaries
{
    /// <summary>
    /// Pulled back from the first word so the opening consonant is not clipped. Starting
    /// exactly on a segment boundary shears the plosive off "P—" and sounds like a glitch.
    /// </summary>
    private const double LeadInSeconds = 0.12;

    /// <summary>
    /// Held after the last word so the sentence lands. Cutting on the final syllable reads
    /// as the video breaking rather than the thought finishing.
    /// </summary>
    private const double TailSeconds = 0.30;

    /// <summary>
    /// Rewrites each clip's in/out points from its segment ids, and attaches the composite
    /// score. Clips the model returned without usable segment ids keep whatever timings it
    /// gave, so an older-shaped response still validates rather than vanishing.
    /// </summary>
    public static JsonElement Apply(JsonElement json, TranscriptContent transcript)
    {
        if (json.ValueKind != JsonValueKind.Object
            || JsonNode.Parse(json.GetRawText()) is not JsonObject root
            || root["clips"] is not JsonArray clips)
        {
            return json;
        }

        var byId = transcript.Segments.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var sourceEnd = transcript.Segments.Count == 0 ? 0 : transcript.Segments.Max(s => s.EndSeconds);

        var kept = new JsonArray();
        foreach (var node in clips.OfType<JsonObject>().ToList())
        {
            if (Resolve(node, byId) is { } span)
            {
                node["inSeconds"] = Math.Round(Math.Max(0, span.Start - LeadInSeconds), 2);
                node["outSeconds"] = Math.Round(Math.Min(sourceEnd, span.End + TailSeconds), 2);
            }
            else if (node["inSeconds"] is null || node["outSeconds"] is null)
            {
                // Named segments that do not exist and no timings to fall back on. Dropping
                // the one clip beats failing the whole artifact on it — the same
                // partial-failure rule the fan-out uses (ADR-006). If every clip is like
                // this the validator's minItems check still sinks the run, correctly.
                continue;
            }

            // Scored after the timings land, because length is part of the score.
            node["score"] = CompositeScore(node);
            kept.Add(node.DeepClone());
        }

        root["clips"] = kept;

        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Resolves the clip's declared segment span to a time range. Accepts either an explicit
    /// <c>segmentIds</c> array or a <c>startSegmentId</c>/<c>endSegmentId</c> pair, because
    /// models drift between the two shapes and both are unambiguous.
    /// </summary>
    private static (double Start, double End)? Resolve(
        JsonObject clip, Dictionary<string, TranscriptSegment> byId)
    {
        var ids = new List<string>();

        if (clip["segmentIds"] is JsonArray array)
        {
            ids.AddRange(array.Select(n => n?.GetValue<string>()).OfType<string>());
        }

        if (clip["startSegmentId"]?.GetValue<string>() is { Length: > 0 } first)
        {
            ids.Add(first);
        }
        if (clip["endSegmentId"]?.GetValue<string>() is { Length: > 0 } last)
        {
            ids.Add(last);
        }

        var resolved = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (resolved.Count == 0)
        {
            return null;
        }

        return (resolved.Min(s => s.StartSeconds), resolved.Max(s => s.EndSeconds));
    }

    /// <summary>
    /// The number shown on a clip row. The model nominates and self-scores; this blends
    /// those judgements with the length, which is the one signal we can measure rather than
    /// ask about. Deliberately OUR arithmetic over the model's own single number: no vendor
    /// publishes a validation study for a "virality score", so this is presented as a
    /// suggested ordering, and an ordering we can at least explain and test.
    /// </summary>
    internal static int CompositeScore(JsonObject clip)
    {
        var scores = clip["scores"] as JsonObject;
        double Read(string name) =>
            scores?[name] is { } value && double.TryParse(value.ToString(), out var parsed)
                ? Math.Clamp(parsed, 0, 10)
                : 5;

        // Hook and payoff weigh most: whether someone stops scrolling, and whether they feel
        // the clip finished. Self-containedness matters because a clip needing setup is
        // unusable however good the moment was.
        var judged = (Read("hook") * 0.35) + (Read("payoff") * 0.25)
                   + (Read("selfContained") * 0.25) + (Read("emotion") * 0.15);

        var length = Length(clip);
        return (int)Math.Round(Math.Clamp(judged * 10 * LengthFactor(length), 0, 100));
    }

    private static double Length(JsonObject clip)
    {
        var inS = clip["inSeconds"];
        var outS = clip["outSeconds"];
        return inS is not null && outS is not null
               && double.TryParse(inS.ToString(), out var start)
               && double.TryParse(outS.ToString(), out var end)
            ? end - start
            : 0;
    }

    /// <summary>
    /// Length as a multiplier rather than a filter. 20–45s is the widely cited sweet spot;
    /// a clip outside the publishable window is still a real moment a human can trim, so it
    /// is ranked lower rather than thrown away — the same call the validator makes when it
    /// warns instead of failing the run.
    /// </summary>
    private static double LengthFactor(double seconds) => seconds switch
    {
        >= 20 and <= 45 => 1.0,
        >= Generators.MinClipSeconds and <= Generators.MaxClipSeconds => 0.9,
        0 => 1.0,
        _ => 0.65,
    };
}
