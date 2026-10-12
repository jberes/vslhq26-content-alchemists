namespace Castmill.Core.Ai;

/// <summary>
/// Who writes every piece of text (ADR-086): the Foundry deployments behind the model aliases,
/// or one OpenAI model called directly with the producer's own key. Stored as plaintext user
/// settings; the key itself lives in the encrypted secret store (<c>OpenAiImageKey</c>).
/// </summary>
public static class TextGenerationSettings
{
    public const string ProviderKey = "ai.text.provider";
    public const string ModelKey = "ai.text.model";
    public const string EffortKey = "ai.text.effort";
    public const string MaxOutputTokensKey = "ai.text.max-output-tokens";

    public const string Foundry = "foundry";
    public const string OpenAi = "openai";

    public const string DefaultOpenAiModel = "gpt-6.1-sol";

    /// <summary>The highest reasoning effort gpt-6.1-sol accepts.</summary>
    public const string DefaultEffort = "max";

    /// <summary>gpt-6.1-sol's output ceiling; reasoning tokens count against it too.</summary>
    public const int DefaultMaxOutputTokens = 128_000;

    public const int MinOutputTokens = 1_024;

    /// <summary>Reasoning efforts in ascending order. Not every model accepts every value.</summary>
    public static readonly IReadOnlyList<string> Efforts = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>What a person calls each effort; the stored value stays the API's.</summary>
    public static string EffortLabel(string effort) => effort switch
    {
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "Extra High",
        "max" => "Max",
        _ => effort,
    };

    /// <summary>The trade each effort makes, for the picker.</summary>
    public static string EffortHint(string effort) => effort switch
    {
        "low" => "fastest, lightest reasoning",
        "medium" => "the model's default balance",
        "high" => "deeper reasoning, slower",
        "xhigh" => "much deeper reasoning, several times slower",
        "max" => "deepest reasoning, slowest — minutes per piece",
        _ => string.Empty,
    };

    public static TextGenerationChoice Read(IEnumerable<KeyValuePair<string, string>> settings)
    {
        var map = settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase);
        var provider = map.GetValueOrDefault(ProviderKey) is { } p
            && p.Equals(OpenAi, StringComparison.OrdinalIgnoreCase) ? OpenAi : Foundry;
        var model = map.GetValueOrDefault(ModelKey) is { Length: > 0 } m ? m.Trim() : DefaultOpenAiModel;
        var effort = map.GetValueOrDefault(EffortKey) is { } e
            && Efforts.Contains(e.Trim(), StringComparer.OrdinalIgnoreCase)
                ? e.Trim().ToLowerInvariant()
                : DefaultEffort;
        var maxOutput = int.TryParse(map.GetValueOrDefault(MaxOutputTokensKey), out var tokens)
            && tokens >= MinOutputTokens
                ? tokens
                : DefaultMaxOutputTokens;
        return new TextGenerationChoice(provider, model, effort, maxOutput);
    }
}

public sealed record TextGenerationChoice(string Provider, string Model, string Effort, int MaxOutputTokens)
{
    public bool UsesOpenAi => Provider == TextGenerationSettings.OpenAi;
}

/// <summary>What <c>/ai/status</c> reports about the text route, so Settings can show it.</summary>
public sealed record TextGenerationStatus(
    string Provider, string Model, string Effort, int MaxOutputTokens, bool Ready, string? Reason);
