namespace Castmill.Api.Services.Ai;

public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public FoundryOptions Foundry { get; set; } = new();
    /// <summary>
    /// Alias → deployment name. The code only ever speaks aliases (G4).
    /// A value may be prefixed "resourceName:deployment" to route the alias to
    /// a named entry in <see cref="Resources"/> — deployments often live on
    /// different Foundry resources (regions/quota), each with its own endpoint+key.
    /// </summary>
    public Dictionary<string, string> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Additional named Foundry resources referenced by "name:deployment" aliases.</summary>
    public Dictionary<string, FoundryOptions> Resources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public SpeechOptions Speech { get; set; } = new();

    public sealed class FoundryOptions
    {
        public string Endpoint { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
    }

    public sealed class SpeechOptions
    {
        public string Region { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Region) && !string.IsNullOrWhiteSpace(Key);
    }
}
