using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Services.Ai.Agents;

/// <summary>The art director's verdict on one render (ADR-058).</summary>
public sealed record CriticVerdict(bool Accept, int Score, IReadOnlyList<string> Defects, string? Fix, bool Ran)
{
    public string Summary => Ran
        ? $"art director {Score}/100" + (Defects.Count == 0 ? "" : " — " + string.Join("; ", Defects.Take(3)))
        : "art director unavailable";
}

public interface IImageCritic
{
    /// <summary>Judges a rendered take against the brief. Never throws: an unavailable vision model returns <c>Ran = false</c>.</summary>
    Task<CriticVerdict> ReviewAsync(Guid userId, string brief, string slotKind, byte[] webp, CancellationToken ct);
}

/// <summary>
/// The image critic (ADR-058): a vision call that checks the one thing a text prompt cannot —
/// what actually got painted. It looks for the recurring defects (painted text, a clipped or
/// missing subject, a busy headline zone, invented product UI) and names the fix, so the
/// render loop can re-prompt with the specific problem instead of rolling the dice again.
/// </summary>
public sealed class ImageCriticAgent(
    IChatProviderRegistry chatProviders,
    IOptions<AiOptions> options,
    IPromptLog promptLog,
    TimeProvider clock,
    ILogger<ImageCriticAgent> logger) : IImageCritic
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CriticVerdict> ReviewAsync(Guid userId, string brief, string slotKind, byte[] webp, CancellationToken ct)
    {
        var settings = options.Value.Agents.ImageCritic;
        var stopwatch = Stopwatch.StartNew();
        var prompt = BuildPrompt(brief, slotKind, settings.AcceptScore);
        var responseText = string.Empty;
        var success = false;
        try
        {
            // "chat-audit" is the cross-check alias already used for drafts; it falls back to "chat".
            var client = await chatProviders.ResolveAsync(userId, "chat-audit", ct);
            var message = new ChatMessage(ChatRole.User,
            [
                new TextContent(prompt),
                new DataContent(webp, "image/webp"),
            ]);
            var response = await client.GetResponseAsync([message], cancellationToken: ct);
            responseText = response.Text;
            success = true;
            return Parse(AiOrchestrator.ParseModelJson(responseText), settings.AcceptScore);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A chat deployment without vision, a JSON slip, a timeout: the take is kept as is.
            logger.LogWarning(ex, "Image critic unavailable; keeping the take unjudged");
            return new CriticVerdict(true, 0, [], null, Ran: false);
        }
        finally
        {
            promptLog.Record(new PromptLogEntry(
                clock.GetUtcNow(), userId, $"{slotKind}-critic", "chat-audit",
                prompt.Length <= 600 ? prompt : prompt[..600] + "…",
                responseText.Length <= 600 ? responseText : responseText[..600] + "…",
                success, stopwatch.ElapsedMilliseconds));
        }
    }

    internal static string BuildPrompt(string brief, string slotKind, int acceptScore) => $$"""
        You are the art director reviewing a generated {{slotKind}} against its brief. Judge the
        IMAGE, not the brief. Check, in this order:
        1. Subject: is the thing the brief asks for actually present, complete and not cut off
           at any edge?
        2. Painted text: any letters, words, numbers, captions, badges or logos the model
           painted? (Text visible inside a real product screenshot the brief attached is fine.)
        3. Headline zone: for a thumbnail or header, is the lower third / left side calm enough
           for a composited headline, or is it busy?
        4. Product fidelity: if a product interface appears, does it look like a real, coherent
           UI rather than invented panels, garbled rows or fake controls?
        5. Craft: lighting, focus, composition, no artefacts (extra fingers, warped objects).

        Score 0–100. Accept at {{acceptScore}} or above. When you reject, write ONE "fix"
        sentence the image model can act on, naming the defect and what to do instead
        (e.g. "Remove all painted text from the lower third and keep it a plain gradient.").

        Reply with JSON only:
        { "score": number, "accept": boolean, "defects": [string], "fix": string|null }

        Brief the image was rendered from:
        {{brief}}
        """;

    internal static CriticVerdict Parse(JsonElement root, int acceptScore)
    {
        var score = root.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number
            ? (int)Math.Clamp(Math.Round(s.GetDouble()), 0, 100)
            : 0;
        var defects = root.TryGetProperty("defects", out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).Take(6).ToList()
            : [];
        var fix = root.TryGetProperty("fix", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()?.Trim() : null;
        // The score is the policy: a model that says "accept" at 40/100 is overruled. A model
        // that scores above the bar but still says "reject" is honoured — it saw something.
        var says = root.TryGetProperty("accept", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? a.ValueKind == JsonValueKind.True
            : score >= acceptScore;
        var accept = says && score >= acceptScore;
        return new CriticVerdict(accept, score, defects, string.IsNullOrWhiteSpace(fix) ? null : fix, Ran: true);
    }
}
