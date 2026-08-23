using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Castmill.Core.Ai;
using Castmill.Core.Resources;

namespace Castmill.Api.Services.Ai;

public sealed class GenerationEvidenceException(string message) : Exception(message);

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

    public static GenerationEvidenceContext FromTranscript(TranscriptContent transcript) =>
        new(
            transcript,
            transcript.Segments.Select(segment => new GenerationEvidenceBlock(
                null,
                string.IsNullOrWhiteSpace(segment.SourceLabel)
                    ? transcript.Source
                    : segment.SourceLabel,
                segment.Id,
                segment.Text,
                "legacy-transcript-segment",
                "{}"))
            .ToList(),
            [],
            transcript.SourceAssetId);

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

    public string ToPromptText()
    {
        if (Blocks.Count > 1_000 || Blocks.Sum(block => (long)block.Content.Length) > 400_000)
        {
            throw new GenerationEvidenceException(
                "Approved evidence exceeds the generation context limit; exclude or split sources before generating.");
        }
        var prompt = new StringBuilder();
        foreach (var block in Blocks)
        {
            prompt.Append("Citation ID: ").AppendLine(block.CitationId);
            prompt.Append("Source: ").AppendLine(block.SourceLabel);
            prompt.Append("Locator: ").Append(block.LocatorKind).Append(' ')
                .AppendLine(block.LocatorJson);
            prompt.Append("Content: ").AppendLine(block.Content);
            prompt.AppendLine();
        }
        return prompt.ToString();
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
            if (exact is null)
            {
                error = $"Citation references unknown approved evidence: {value}.";
                return false;
            }
            canonical = exact.CitationId;
            return true;
        }

        var matches = Blocks.Where(block =>
                string.Equals(block.StableId, value, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        if (matches.Count == 0)
        {
            error = $"Citation references unknown approved evidence: {value}.";
            return false;
        }
        if (matches.Count > 1)
        {
            error = $"Citation '{value}' is ambiguous across approved sources; use its qualified evidence id.";
            return false;
        }

        canonical = matches[0].CitationId;
        return true;
    }
}