using System.Text;
using System.Text.Json;
using Castmill.Core.Resources;

namespace Castmill.Api.Services.Ai;

/// <summary>
/// The technical brief (ADR-056) as stored JSON and as prompt text. It rides on the campaign
/// brief string so every existing generation path (fan-out, one kind, regenerate, Tech Edit)
/// receives it without a second parameter threaded through the orchestrator.
/// </summary>
public static class TechnicalBriefs
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static TechnicalBrief? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var brief = JsonSerializer.Deserialize<TechnicalBrief>(json, Json);
            return brief is null || brief.IsEmpty ? null : brief;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? Serialize(TechnicalBrief? brief) =>
        brief is null || brief.IsEmpty ? null : JsonSerializer.Serialize(brief, Json);

    /// <summary>The brief as an instruction block the model reads after the campaign brief.</summary>
    public static string? ToPromptBlock(TechnicalBrief? brief)
    {
        if (brief is null || brief.IsEmpty)
        {
            return null;
        }
        var text = new StringBuilder();
        text.AppendLine("TECHNICAL BRIEF (authoritative facts — never contradict, never embellish):");
        Line(text, "Product", brief.Product);
        Line(text, "Version", brief.Version);
        Line(text, "APIs, components or commands to reference exactly", brief.Apis);
        Line(text, "Constraints and known limitations", brief.Constraints);
        Line(text, "Must mention", brief.MustMention);
        Line(text, "Must NOT claim", brief.MustNotClaim);
        text.AppendLine(
            "Every technical statement must be traceable to this brief, the approved evidence, or a "
            + "knowledge-base/skill/MCP result. State uncertainty rather than inventing detail.");
        text.AppendLine("END TECHNICAL BRIEF");
        return text.ToString().TrimEnd();
    }

    /// <summary>Appends the brief block to a steering/brief string; either may be null.</summary>
    public static string? Merge(string? brief, TechnicalBrief? technical)
    {
        var block = ToPromptBlock(technical);
        if (block is null)
        {
            return brief;
        }
        return string.IsNullOrWhiteSpace(brief) ? block : $"{brief.Trim()}\n\n{block}";
    }

    /// <summary>Knowledge-base query seed: product, version and the APIs named in the brief.</summary>
    public static string? QuerySeed(TechnicalBrief? brief)
    {
        if (brief is null || brief.IsEmpty)
        {
            return null;
        }
        var parts = new[] { brief.Product, brief.Version, brief.Apis }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim());
        var seed = string.Join(" · ", parts);
        return seed.Length == 0 ? null : seed.Length > 400 ? seed[..400] : seed;
    }

    private static void Line(StringBuilder text, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            text.Append("- ").Append(label).Append(": ").AppendLine(value.Trim());
        }
    }
}
