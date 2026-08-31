using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Images;
using SkiaSharp;

namespace Castmill.Api.Tests;

/// <summary>
/// Deliberately outside the "api" collection: these are pure unit tests over the render
/// pipeline's prompt handling, so they must run without Docker/Testcontainers.
/// </summary>
public sealed class ImagePromptRulesTests
{
    /// <summary>
    /// Every render centre-crops to the slot's aspect (providers emit only a fixed size set),
    /// so anything near an edge is cut off. The safe-margin rule must reach the provider on
    /// EVERY path — including a raw user-authored prompt that says nothing about margins.
    /// </summary>
    [Fact]
    public async Task Every_render_path_sends_the_safe_margin_rule_to_the_provider()
    {
        var provider = new PromptCapturingProvider();
        var renderer = new ImageRenderer(new SingleProviderRegistry(provider), new PassThroughComposer());

        await renderer.RenderWebpAsync(Guid.NewGuid(), "a hero image", "16:9", "foundry", default);
        await renderer.RenderExactAsync(Guid.NewGuid(), "a hero image", 1280, 720, "foundry", default);
        await renderer.RenderExactAsync(Guid.NewGuid(), "a hero image", 1280, 720, "foundry", [], default);

        Assert.Equal(3, provider.Prompts.Count);
        Assert.All(provider.Prompts, prompt =>
        {
            Assert.StartsWith("a hero image", prompt, StringComparison.Ordinal);
            Assert.Contains(ImagePromptRules.Composition, prompt, StringComparison.Ordinal);
            Assert.Contains($"{ImagePromptRules.SafeMarginPercent}% of any edge", prompt, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_rule_states_a_margin_wider_than_the_worst_case_crop()
    {
        // Worst case in the slot catalogue: a 1600x840 header from the provider's 1536x1024.
        var scale = Math.Max(1600f / 1536f, 840f / 1024f);
        var scaledHeight = 1024f * scale;
        var lostPerEdgePercent = (scaledHeight - 840f) / 2f / scaledHeight * 100f;

        Assert.True(
            ImagePromptRules.SafeMarginPercent > lostPerEdgePercent,
            $"Safe margin {ImagePromptRules.SafeMarginPercent}% must exceed the {lostPerEdgePercent:0.0}% lost per edge.");
    }

    [Fact]
    public void A_blank_prompt_is_left_alone()
    {
        Assert.Equal(string.Empty, ImagePromptRules.Apply(string.Empty));
        Assert.Equal("   ", ImagePromptRules.Apply("   "));
    }

    private sealed class PromptCapturingProvider : IImageProvider
    {
        // RenderWebpAsync really decodes the provider's bytes, so hand back a real image.
        private static readonly byte[] Png = EncodePng();

        public List<string> Prompts { get; } = [];

        public string Name => "capture";

        public Task<ImageProviderStatus> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult(new ImageProviderStatus(Name, true, null));

        public Task<byte[]> GenerateAsync(
            Guid userId, string prompt, string aspectRatio, string? modelAlias, CancellationToken ct)
        {
            Prompts.Add(prompt);
            return Task.FromResult(Png);
        }

        private static byte[] EncodePng()
        {
            using var bitmap = new SKBitmap(8, 8);
            bitmap.Erase(SKColors.Coral);
            using var image = SKImage.FromBitmap(bitmap);
            return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        }

        public Task<byte[]> GenerateAsync(
            Guid userId, string prompt, string aspectRatio, string? modelAlias,
            IReadOnlyList<ImageReference> references, CancellationToken ct) =>
            GenerateAsync(userId, prompt, aspectRatio, modelAlias, ct);
    }

    private sealed class SingleProviderRegistry(IImageProvider provider) : IImageProviderRegistry
    {
        public IImageProvider Resolve(string? modelAliasOrProvider) => provider;

        public Task<IReadOnlyList<ImageProviderStatus>> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ImageProviderStatus>>([]);
    }

    /// <summary>Keeps the prompt assertions independent of real Skia decoding.</summary>
    private sealed class PassThroughComposer : IImageComposer
    {
        public byte[] ToSlotWebp(byte[] sourceImage, int width, int height) => sourceImage;

        public CompositeResult ComposeHeadline(
            byte[] image, string headline, bool safeArea, string? backgroundColor = null) =>
            new(image, false, "test");

        public byte[] ToThumbWebp(byte[] webpImage, int maxEdge = 480) => webpImage;
    }
}
