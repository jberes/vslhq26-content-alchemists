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
        /// <summary>
        /// Feature flag. A provider the config invents is never resolvable unless it says
        /// Enabled=true; the built-in alternates default to on. Nullable so that a config
        /// entry which pins only a model cannot read as "Enabled=false".
        /// </summary>
        public bool? Enabled { get; set; }
        public string Endpoint { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        /// <summary>
        /// Wire protocol: "openai" (images/generations + images/edits) or "gemini"
        /// (models/{model}:generateContent, Nano Banana). Not a style preference — the two
        /// request/response shapes have nothing in common.
        /// </summary>
        public string Kind { get; set; } = "openai";
        /// <summary>
        /// Which encrypted per-user secret holds this provider's API key. One slot per
        /// vendor so a workspace can hold a Gemini key AND an OpenAI key at once; the old
        /// shared <see cref="Secrets.SecretKind.ImageProviderKey"/> stays as a fallback.
        /// </summary>
        public Secrets.SecretKind Credential { get; set; } = Secrets.SecretKind.ImageProviderKey;
    }

    /// <summary>
    /// The alternates that ship with the product (ADR-015 addendum, ADR-026): Nano Banana
    /// and OpenAI's gpt-image are named, first-class choices in the studio's model picker
    /// rather than something to discover by hand-editing config. They are still inert until
    /// a key is stored — the credential is the gate, not the flag — and config may override
    /// any field or set Enabled=false to remove one entirely.
    /// </summary>
    public static IReadOnlyDictionary<string, ImageProviderOptions> MergeImageProviders(
        IDictionary<string, ImageProviderOptions> configured)
    {
        var merged = new Dictionary<string, ImageProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["nano-banana"] = new()
            {
                Enabled = true,
                Kind = "gemini",
                Endpoint = "https://generativelanguage.googleapis.com/v1beta",
                Model = "gemini-2.5-flash-image",
                Credential = Secrets.SecretKind.NanoBananaKey,
            },
            ["gpt-image"] = new()
            {
                Enabled = true,
                Kind = "openai",
                Endpoint = "https://api.openai.com/v1",
                Model = "gpt-image-1",
                Credential = Secrets.SecretKind.OpenAiImageKey,
            },
        };

        foreach (var (name, options) in configured)
        {
            if (!merged.TryGetValue(name, out var builtIn))
            {
                // An invented provider stays opt-in (ADR-015).
                options.Enabled ??= false;
                merged[name] = options;
                continue;
            }

            // Field-level override: a config file that only pins a model must not blank the
            // built-in endpoint, its credential slot, or its enabled state.
            builtIn.Enabled = options.Enabled ?? builtIn.Enabled;
            builtIn.Kind = string.IsNullOrWhiteSpace(options.Kind) ? builtIn.Kind : options.Kind;
            builtIn.Endpoint = string.IsNullOrWhiteSpace(options.Endpoint) ? builtIn.Endpoint : options.Endpoint;
            builtIn.Model = string.IsNullOrWhiteSpace(options.Model) ? builtIn.Model : options.Model;
            builtIn.Credential = options.Credential == Secrets.SecretKind.ImageProviderKey
                ? builtIn.Credential
                : options.Credential;
        }
        return merged;
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
        public string Endpoint { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint)
            || (!string.IsNullOrWhiteSpace(Region) && !string.IsNullOrWhiteSpace(Key));
    }
}
