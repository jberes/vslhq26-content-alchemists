using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Resources;

namespace Castmill.Api.Services.Ai;

public sealed class GenerationEvidenceException(string message) : Exception(message);

/// <summary>
/// The model's reply could not be read as JSON. Derives from <see cref="JsonException"/> so the
/// callers that already treat unparseable output as "no result" keep working, but carries a
/// message a producer can act on — "JsonReaderException" (System.Text.Json's internal parse
/// type) was what reached the screen before (ADR-066).
/// </summary>
public sealed class ModelJsonException(string message) : JsonException(message);

public sealed record GenerationEvidenceBlock(
    Guid? SourceAssetId,
    string SourceLabel,
    string StableId,
    string Content,
    string LocatorKind,
    string LocatorJson)
{
    public string CitationId => SourceAssetId is { } sourceAssetId
        ? CitationReferenceCodec.Format(sourceAssetId, StableId)
        : StableId;
}

public sealed record GenerationEvidenceContext(
    TranscriptContent Transcript,
    IReadOnlyList<GenerationEvidenceBlock> Blocks,
    IReadOnlyList<ApprovedEvidenceRevision> ApprovedRevisions,
    Guid? TranscriptSourceAssetId)
{
    public GenerationEvidenceContext(
        TranscriptContent transcript,
        IReadOnlyList<GenerationEvidenceBlock> blocks)
        : this(transcript, blocks, [], null)
    {
    }

    public const string LegacyTranscriptSegment = "legacy-transcript-segment";

    public static GenerationEvidenceContext FromTranscript(TranscriptContent transcript)
    {
        // Real timings ride along, so the legacy path reads as timestamped paragraphs too.
        var timed = transcript.Segments.Any(segment => segment.EndSeconds > 0);
        return new(
            transcript,
            transcript.Segments.Select(segment => new GenerationEvidenceBlock(
                null,
                string.IsNullOrWhiteSpace(segment.SourceLabel)
                    ? transcript.Source
                    : segment.SourceLabel,
                segment.Id,
                segment.Text,
                LegacyTranscriptSegment,
                timed
                    ? JsonSerializer.Serialize(new { startSeconds = segment.StartSeconds, endSeconds = segment.EndSeconds })
                    : "{}"))
            .ToList(),
            [],
            transcript.SourceAssetId);
    }

    /// <summary>Sentence-sized transcript blocks — rendered as paragraphs, not records (ADR-087).</summary>
    private static bool IsTranscriptSegment(string locatorKind) =>
        locatorKind is EvidenceLocatorKinds.MediaTimeRange
            or EvidenceLocatorKinds.TextSegment
            or LegacyTranscriptSegment;

    public GenerationEvidenceContext ForSelectedTranscript()
    {
        if (TranscriptSourceAssetId is not { } sourceAssetId)
        {
            if (Blocks.Any(block => block.SourceAssetId is not null))
            {
                throw new GenerationEvidenceException(
                    "The selected transcript is not linked to approved source evidence.");
            }
            return this;
        }

        return this with
        {
            Blocks = Blocks.Where(block => block.SourceAssetId == sourceAssetId).ToList(),
            ApprovedRevisions = ApprovedRevisions
                .Where(revision => revision.SourceAssetId == sourceAssetId)
                .ToList(),
        };
    }

    public bool TryNormalizeCitations(
        JsonElement json,
        out JsonElement normalized,
        out string? error)
    {
        normalized = json;
        error = null;
        if (!json.TryGetProperty("citations", out var citations)
            || citations.ValueKind != JsonValueKind.Array)
        {
            error = "Missing required 'citations' array (provenance contract).";
            return false;
        }

        var resolved = new List<string>();
        foreach (var citation in citations.EnumerateArray())
        {
            if (citation.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(citation.GetString()))
            {
                error = "Every citation must be a non-empty evidence id.";
                return false;
            }

            if (!TryResolve(citation.GetString()!, out var canonical, out error))
            {
                return false;
            }
            resolved.Add(canonical);
        }

        if (resolved.Count == 0)
        {
            error = "At least one citation is required.";
            return false;
        }

        var root = JsonNode.Parse(json.GetRawText())?.AsObject()
            ?? throw new JsonException("Generator output must be a JSON object.");
        root["citations"] = new JsonArray(resolved
            .Distinct(StringComparer.Ordinal)
            .Select(value => (JsonNode?)JsonValue.Create(value))
            .ToArray());
        using var document = JsonDocument.Parse(root.ToJsonString());
        normalized = document.RootElement.Clone();
        return true;
    }

    /// <summary>
    /// The budget is the rendered evidence, not the block count. A transcript block is one
    /// short caption segment, so an hour-long webinar is ~1,000 blocks of only ~50,000
    /// characters — a block cap refused it outright. 700,000 is roughly what the import caps
    /// (1,000 blocks, 400,000 characters) could already render, citation lines included.
    /// </summary>
    internal const int MaxPromptCharacters = 700_000;

    public string ToPromptText()
    {
        var prompt = new StringBuilder();
        for (var index = 0; index < Blocks.Count;)
        {
            if (IsTranscriptSegment(Blocks[index].LocatorKind))
            {
                index = AppendTimedTranscript(prompt, index);
            }
            else
            {
                AppendBlock(prompt, Blocks[index]);
                index++;
            }
            if (prompt.Length > MaxPromptCharacters)
            {
                throw new GenerationEvidenceException(
                    "Approved evidence exceeds the generation context limit; exclude or split sources before generating.");
            }
        }
        return prompt.ToString();
    }

    private static void AppendBlock(StringBuilder prompt, GenerationEvidenceBlock block)
    {
        prompt.Append("Citation ID: ").AppendLine(block.CitationId);
        prompt.Append("Source: ").AppendLine(block.SourceLabel);
        prompt.Append("Locator: ").Append(block.LocatorKind).Append(' ')
            .AppendLine(block.LocatorJson);
        prompt.Append("Content: ").AppendLine(block.Content);
        prompt.AppendLine();
    }

    /// <summary>
    /// A recording's run of caption blocks as readable, timestamped paragraphs (ADR-087). One
    /// block per caption fragment, each with its own citation, source and locator lines, made
    /// two thirds of an hour-long webinar's prompt bookkeeping and left the model reading
    /// 1,000 shards instead of a talk. Every block keeps its id inline, so citations and clip
    /// boundaries stay exact; the qualified id is the source's prefix plus that id.
    /// </summary>
    private int AppendTimedTranscript(StringBuilder prompt, int start)
    {
        var first = Blocks[start];
        var prefix = first.CitationId[..^first.StableId.Length];
        prompt.Append("Source: ").Append(first.SourceLabel)
            .AppendLine(first.LocatorKind == EvidenceLocatorKinds.TextSegment ? " — transcript" : " — timed transcript");
        prompt.AppendLine(prefix.Length == 0
            ? "Cite a passage by the bracketed id that precedes it."
            : $"Cite a passage as {prefix}<id>, using the bracketed id that precedes it (e.g. {first.CitationId}).");

        var index = start;
        double? paragraphStart = null;
        var paragraphLength = 0;
        while (index < Blocks.Count
            && IsTranscriptSegment(Blocks[index].LocatorKind)
            && Blocks[index].SourceAssetId == first.SourceAssetId
            && Blocks[index].SourceLabel == first.SourceLabel)
        {
            var block = Blocks[index];
            var seconds = StartSeconds(block.LocatorJson);
            // A new paragraph about every minute of talk, so the model reads sections, not a wall.
            if (paragraphLength > 0 && (paragraphLength > 900
                || (seconds is { } at && paragraphStart is { } began && at - began >= 60)))
            {
                prompt.AppendLine();
                paragraphLength = 0;
            }
            if (paragraphLength == 0)
            {
                paragraphStart = seconds;
            }
            else
            {
                prompt.Append(' ');
            }
            prompt.Append('[').Append(block.StableId);
            if (seconds is { } time)
            {
                prompt.Append(' ').Append(Timestamp(time));
            }
            prompt.Append("] ").Append(block.Content.Trim());
            paragraphLength += block.Content.Length;
            index++;
        }
        prompt.AppendLine().AppendLine();
        return index;
    }

    private static double? StartSeconds(string locatorJson)
    {
        try
        {
            using var locator = JsonDocument.Parse(locatorJson);
            return locator.RootElement.ValueKind == JsonValueKind.Object
                && locator.RootElement.TryGetProperty("startSeconds", out var start)
                && start.ValueKind == JsonValueKind.Number
                    ? start.GetDouble()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string Timestamp(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    private bool TryResolve(string value, out string canonical, out string? error)
    {
        canonical = value;
        error = null;
        if (CitationReferenceCodec.TryParse(value, out var qualified))
        {
            var exact = Blocks.SingleOrDefault(block =>
                block.SourceAssetId == qualified.SourceAssetId
                && string.Equals(
                    block.StableId,
                    qualified.EvidenceBlockId,
                    StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                canonical = exact.CitationId;
                return true;
            }
            // Right shape, wrong source: fall through to the block id, which is the part the
            // model actually read off the evidence.
            return TryResolveBlockId(qualified.EvidenceBlockId, value, out canonical, out error);
        }

        // A qualified id whose 32-character source guid the model mis-transcribed. Observed in
        // production: "evidence:b88337dc671846a986e8da611ad94df:s19" for source
        // b88337dc-6718-46a9-863e-8da611ad94df — one character dropped, so the codec rejects it
        // and the whole string became the block id, matching nothing. The block id after the
        // last colon is still the real one, so resolve on that and let the ambiguity check
        // below decide. A citation is only ever accepted when it names a real approved block.
        return TryResolveBlockId(RecoverBlockId(value), value, out canonical, out error);
    }

    /// <summary>The trailing block id of a qualified-looking reference, else the value itself.</summary>
    internal static string RecoverBlockId(string value)
    {
        if (!value.StartsWith("evidence:", StringComparison.Ordinal))
        {
            return value;
        }
        var lastColon = value.LastIndexOf(':');
        return lastColon > 0 && lastColon < value.Length - 1 ? value[(lastColon + 1)..] : value;
    }

    private bool TryResolveBlockId(string blockId, string original, out string canonical, out string? error)
    {
        canonical = original;
        error = null;
        var matches = Blocks.Where(block =>
                string.Equals(block.StableId, blockId, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        if (matches.Count == 0)
        {
            error = $"Citation references unknown approved evidence: {original}.";
            return false;
        }
        if (matches.Count > 1)
        {
            error = $"Citation '{original}' is ambiguous across approved sources; use its qualified evidence id.";
            return false;
        }

        canonical = matches[0].CitationId;
        return true;
    }
}