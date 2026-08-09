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
    /// <summary>
    /// Optional non-Foundry <b>image</b> providers (ADR-015), disabled unless
    /// explicitly enabled. Kept separate from <see cref="TextProviders"/>: the image
    /// registration loop registers every entry under this key as an image provider,
    /// so a text provider filed here would be silently mis-registered.
    /// </summary>
    public Dictionary<string, ImageProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Optional non-Foundry <b>text</b> providers (ADR-020), disabled unless explicitly
    /// enabled. Foundry remains the only provider for pass-1 generation and the whole
    /// fan-out; this map exists for the second-pass Tech Edit, which is deliberately a
    /// different model family on a different key.
    /// </summary>
    public Dictionary<string, TextProviderOptions> TextProviders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public SpeechOptions Speech { get; set; } = new();
    /// <summary>REST version used for multipart image edits (reference-image generation).</summary>
    public string ImageApiVersion { get; set; } = "2025-04-01-preview";

    public sealed class FoundryOptions
    {
        public string Endpoint { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
    }

    public sealed class ImageProviderOptions
    {
        /// <summary>Feature flag — a provider that isn't explicitly enabled is never resolvable.</summary>
        public bool Enabled { get; set; }
        public string Endpoint { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
    }

    public sealed class TextProviderOptions
    {
        /// <summary>Feature flag — a provider that isn't explicitly enabled is never resolvable.</summary>
        public bool Enabled { get; set; }
        /// <summary>Provider family. Only "anthropic" is implemented (ADR-020).</summary>
        public string Kind { get; set; } = string.Empty;
        /// <summary>Model id passed to the provider SDK, e.g. "claude-opus-5".</summary>
        public string Model { get; set; } = string.Empty;
    }

    public sealed class SpeechOptions
    {
        public string Region { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Region) && !string.IsNullOrWhiteSpace(Key);
    }
}
