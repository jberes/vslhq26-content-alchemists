using System.Net;
using System.Net.Http.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Images;
using Castmill.Api.Services.Secrets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Castmill.Api.Tests;

/// <summary>
/// The image-provider seam (ADR-015 / ADR-026), tested at the wire: which parameters go out,
/// what comes back, and what a producer is told when a provider says no.
///
/// Deliberately NOT in the "api" collection — these need no database and no Docker, so the
/// regressions they cover stay runnable in any environment. The reference-image test used to
/// live behind the Testcontainers fixture, which is part of why a 100%-reproducible
/// generation failure went five rounds without being caught.
/// </summary>
public sealed class ImageProviderTests
{
    private static byte[] SolidPng(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Fact]
    public async Task Foundry_reference_generation_posts_real_images_to_the_edits_endpoint()
    {
        var handler = new CapturingImageHandler();
        // gpt-image-1 is the deployment that DOES accept input_fidelity.
        var provider = NewFoundryProvider(handler, deployment: "gpt-image-1");
        var png = SolidPng(16, 16, SKColors.Blue);

        var result = await provider.GenerateAsync(Guid.NewGuid(), "faithful product screenshot", "16:9", null,
            [new ImageReference(Guid.NewGuid(), "product.png", "image/png", png, "product")],
            TestContext.Current.CancellationToken);

        Assert.Equal(png, result);
        Assert.Contains("/openai/deployments/gpt-image-1/images/edits", handler.RequestUri, StringComparison.Ordinal);
        Assert.Contains("api-version=2025-04-01-preview", handler.RequestUri, StringComparison.Ordinal);
        Assert.Contains("name=\"image[]\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("product.png", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("input_fidelity", handler.LastBody, StringComparison.Ordinal);
        Assert.Equal("secret", handler.ApiKey);
    }

    /// <summary>
    /// The outage this seam was rebuilt for: <c>input_fidelity=high</c> went on EVERY reference
    /// render, gpt-image-2 rejects it with a 400, so every card carrying a brand or product
    /// reference failed 100% of the time while reference-free renders kept working — which is
    /// what made a deterministic bug look intermittent.
    /// </summary>
    [Fact]
    public async Task Input_fidelity_is_not_sent_to_a_model_that_rejects_it()
    {
        var handler = new CapturingImageHandler();
        var provider = NewFoundryProvider(handler, deployment: "gpt-image-2");

        await provider.GenerateAsync(Guid.NewGuid(), "product shot", "16:9", null,
            [new ImageReference(Guid.NewGuid(), "product.png", "image/png", SolidPng(8, 8, SKColors.Red), "product")],
            TestContext.Current.CancellationToken);

        Assert.Single(handler.Bodies); // no wasted round trip: it was never asked for
        Assert.DoesNotContain("input_fidelity", handler.LastBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// And when a model we have no seeded knowledge of refuses a parameter by name, the request
    /// is retried once without it — a new deployment costs one 400, not an outage.
    /// </summary>
    [Fact]
    public async Task A_parameter_the_model_refuses_is_dropped_and_the_render_retried()
    {
        var handler = new CapturingImageHandler
        {
            FirstResponse = (HttpStatusCode.BadRequest, """
                {"error":{"message":"The model 'gpt-image-1' does not support the 'input_fidelity' parameter.",
                          "type":"image_generation_user_error","param":"input_fidelity",
                          "code":"invalid_input_fidelity_model"}}
                """),
        };
        var capabilities = new ImageModelCapabilities();
        // Seeded as supported for this model name, so the FIRST attempt does send it.
        var provider = NewFoundryProvider(handler, deployment: "gpt-image-1", capabilities);
        var reference = new ImageReference(Guid.NewGuid(), "p.png", "image/png", SolidPng(8, 8, SKColors.Red), "product");

        var result = await provider.GenerateAsync(
            Guid.NewGuid(), "product shot", "16:9", null, [reference], TestContext.Current.CancellationToken);

        Assert.NotEmpty(result);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("input_fidelity", handler.Bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("input_fidelity", handler.Bodies[1], StringComparison.Ordinal);
        // Learned, so no later render for that model pays the failed attempt again.
        Assert.False(capabilities.Supports("gpt-image-1", ImageModelCapabilities.InputFidelity));
    }

    /// <summary>
    /// A provider refusal must reach the producer as the provider's own sentence. The studio
    /// used to print only "InvalidOperationException" — a string nobody can act on.
    /// </summary>
    [Fact]
    public async Task A_provider_refusal_surfaces_the_providers_own_message()
    {
        var handler = new CapturingImageHandler
        {
            FirstResponse = (HttpStatusCode.BadRequest, """
                {"error":{"message":"Invalid size '7x7'. Width and height must both be divisible by 16.",
                          "param":"size","code":"invalid_value"}}
                """),
            RepeatFirstResponse = true,
        };
        var provider = NewFoundryProvider(handler, deployment: "gpt-image-2");

        var ex = await Assert.ThrowsAsync<ImageProviderException>(() => provider.GenerateAsync(
            Guid.NewGuid(), "a probe", "16:9", null, TestContext.Current.CancellationToken));

        Assert.Contains("divisible by 16", ex.Message, StringComparison.Ordinal);
        Assert.Contains("invalid_value", ex.Message, StringComparison.Ordinal);
        // …and the endpoint hands that sentence to the client rather than the type name.
        Assert.Equal(ex.Message, Castmill.Api.Endpoints.ImageSlotEndpoints.FailureReason(ex));
        Assert.DoesNotContain("Exception", Castmill.Api.Endpoints.ImageSlotEndpoints.FailureReason(ex),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A safety refusal is the one failure a producer can act on, so it keeps its advice however
    /// the vendor spells it. Azure OpenAI says <c>moderation_blocked</c>; the MAI surface says
    /// <c>content_safety_violation / violated mainline safety policies</c> — and that second
    /// spelling fell through to a generic message until a live render proved it exists.
    /// </summary>
    [Theory]
    [InlineData("moderation_blocked", "Your request was blocked.")]
    [InlineData("content_safety_violation", "Input content violated mainline safety policies.")]
    [InlineData("content_filter", "The response was filtered by the content management policy.")]
    public async Task A_safety_refusal_is_reported_as_one_however_it_is_spelled(string code, string message)
    {
        var handler = new CapturingImageHandler
        {
            FirstResponse = (HttpStatusCode.BadRequest,
                $"{{\"error\":{{\"message\":\"{message}\",\"code\":\"{code}\"}}}}"),
            RepeatFirstResponse = true,
        };
        var provider = NewFoundryProvider(handler, deployment: "gpt-image-2");

        var ex = await Assert.ThrowsAsync<ImageModerationException>(() => provider.GenerateAsync(
            Guid.NewGuid(), "a probe", "1:1", null, TestContext.Current.CancellationToken));

        Assert.Contains("safety system", ex.Message, StringComparison.Ordinal);
        // It must name the reference images, because that is where the trigger usually is —
        // a live MAI render was declined for a brand FACE asset, not for its prompt.
        Assert.Contains("reference", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ex.Message, Castmill.Api.Endpoints.ImageSlotEndpoints.FailureReason(ex));
    }

    /// <summary>An unparseable error body is never echoed: a provider error can quote the
    /// request it received, and that request carries the credential.</summary>
    [Fact]
    public async Task An_unreadable_error_body_is_never_echoed_to_the_client()
    {
        var handler = new CapturingImageHandler
        {
            FirstResponse = (HttpStatusCode.BadGateway, "<html>api-key=super-secret-value</html>"),
            RepeatFirstResponse = true,
        };
        var provider = NewFoundryProvider(handler, deployment: "gpt-image-2");

        var ex = await Assert.ThrowsAsync<ImageProviderException>(() => provider.GenerateAsync(
            Guid.NewGuid(), "a probe", "1:1", null, TestContext.Current.CancellationToken));

        Assert.DoesNotContain("super-secret-value", ex.Message, StringComparison.Ordinal);
        Assert.Contains("502", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A provider-side HTTP timeout is one take's failure, phrased for a producer —
    /// not an unhandled exception that 500s the batch and leaves the run row stuck "Running".</summary>
    [Fact]
    public void A_provider_timeout_reads_as_a_retryable_take_failure()
    {
        Assert.Equal(
            Castmill.Api.Endpoints.ImageSlotEndpoints.TimedOutReason,
            Castmill.Api.Endpoints.ImageSlotEndpoints.FailureReason(
                new TaskCanceledException("The request was canceled due to timeout.")));
    }

    /// <summary>
    /// Plain generation goes to images/generations over the SAME resilient transport as the
    /// edits path — one timeout policy, one error parser, one repair pass. It used to go
    /// through the typed SDK, whose failures surfaced in a different, opaque shape.
    /// </summary>
    [Fact]
    public async Task Plain_generation_uses_the_rest_generations_route()
    {
        var handler = new CapturingImageHandler();
        var provider = NewFoundryProvider(handler, deployment: "gpt-image-2");

        await provider.GenerateAsync(
            Guid.NewGuid(), "a lighthouse", "9:16", null, TestContext.Current.CancellationToken);

        Assert.Contains("/openai/deployments/gpt-image-2/images/generations", handler.RequestUri,
            StringComparison.Ordinal);
        Assert.Contains("\"size\":\"1024x1536\"", handler.LastBody, StringComparison.Ordinal);
    }

    /// <summary>A URL-shaped response is followed rather than decoded as pixels — that
    /// mismatch surfaced as "the model returned bytes that are not a decodable image".</summary>
    [Fact]
    public async Task A_url_shaped_response_is_downloaded_instead_of_being_decoded()
    {
        var png = SolidPng(10, 10, SKColors.Purple);
        var handler = new UrlResponseHandler(png);
        var provider = NewFoundryProvider(handler, deployment: "dall-e-3");

        var result = await provider.GenerateAsync(
            Guid.NewGuid(), "a lighthouse", "1:1", null, TestContext.Current.CancellationToken);

        Assert.Equal(png, result);
    }

    // ---- Reference normalisation ------------------------------------------------

    /// <summary>
    /// References used to be re-encoded as PNG at quality 100 at their ORIGINAL resolution,
    /// which turned one phone-sized photo into a 34.9 MB attachment — refused outright by MAI
    /// (20 MB cap), and eight of those on a single card is a quarter-gigabyte upload before the
    /// model starts. Generated output is ~1 MP, so the extra pixels carry nothing.
    /// </summary>
    [Fact]
    public void A_large_photographic_reference_is_downscaled_and_sent_as_jpeg()
    {
        // A "photograph": opaque, and far larger than any model needs.
        var original = NoisyPng(4000, 3000, opaque: true);
        Assert.True(original.Length > 2 * 1024 * 1024, $"fixture is only {original.Length} bytes");

        var reference = ImageReferenceResolver.Normalize(Guid.NewGuid(), original, "product");

        Assert.NotNull(reference);
        Assert.Equal("image/jpeg", reference!.ContentType);
        Assert.EndsWith(".jpg", reference.FileName, StringComparison.Ordinal);
        Assert.True(reference.Bytes.Length <= ImageReferenceResolver.MaxReferenceBytes);
        // An order of magnitude smaller is the point, not a rounding win.
        Assert.True(reference.Bytes.Length < original.Length / 4,
            $"{reference.Bytes.Length} bytes is not meaningfully smaller than {original.Length}");

        using var decoded = SKBitmap.Decode(reference.Bytes);
        Assert.Equal(ImageReferenceResolver.MaxReferenceEdge, Math.Max(decoded.Width, decoded.Height));
        Assert.Equal(4000d / 3000d, (double)decoded.Width / decoded.Height, 2); // aspect preserved
    }

    /// <summary>A cut-out logo keeps its alpha — JPEG would fill the transparency with black.</summary>
    [Fact]
    public void A_transparent_logo_stays_png()
    {
        var logo = NoisyPng(2048, 2048, opaque: false);

        var reference = ImageReferenceResolver.Normalize(Guid.NewGuid(), logo, "logo");

        Assert.NotNull(reference);
        Assert.Equal("image/png", reference!.ContentType);
        using var decoded = SKBitmap.Decode(reference.Bytes);
        Assert.False(decoded.Info.IsOpaque);
        Assert.True(Math.Max(decoded.Width, decoded.Height) <= ImageReferenceResolver.MaxReferenceEdge);
    }

    [Fact]
    public void Bytes_that_are_not_an_image_are_not_offered_as_a_reference() =>
        Assert.Null(ImageReferenceResolver.Normalize(Guid.NewGuid(), [1, 2, 3, 4], "product"));

    /// <summary>Noise, not flat colour: a solid fill compresses to nothing and would prove
    /// nothing about size handling.</summary>
    private static byte[] NoisyPng(int width, int height, bool opaque)
    {
        using var bitmap = new SKBitmap(width, height, isOpaque: opaque);
        var state = 12345u;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                state = (state * 1664525u) + 1013904223u;
                var r = (byte)(state >> 16);
                var g = (byte)(state >> 8);
                var b = (byte)state;
                bitmap.SetPixel(x, y, new SKColor(r, g, b, opaque ? (byte)255 : (byte)128));
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    // ---- The MAI image surface (ADR-038) ---------------------------------------

    /// <summary>
    /// MAI-Image-* deployments do NOT answer on `/openai/deployments/{d}/images/…` — that path
    /// 404s even though the deployment is listed and healthy, which is exactly how the
    /// "image-alt" alias looked broken. They speak their own surface: a different hostname, no
    /// api-version, and width/height instead of size.
    /// </summary>
    [Fact]
    public async Task A_mai_deployment_generates_on_the_mai_surface()
    {
        var handler = new CapturingImageHandler();
        var provider = NewFoundryProvider(handler, deployment: "MAI-Image-2.5-Pro");

        await provider.GenerateAsync(
            Guid.NewGuid(), "a lighthouse", "16:9", null, TestContext.Current.CancellationToken);

        Assert.Equal("https://foundry.services.ai.azure.com/mai/v1/images/generations", handler.RequestUri);
        Assert.DoesNotContain("api-version", handler.RequestUri, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"MAI-Image-2.5-Pro\"", handler.LastBody, StringComparison.Ordinal);
        // width/height, never "size" — sending size is what produced a 500 from this surface.
        Assert.Contains("\"width\":1365", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"height\":768", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"size\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Single(handler.Bodies); // the dialect was known up front — no 404 discovery cost
    }

    /// <summary>
    /// The MAI edits endpoint rejects more than one attachment ("Exactly one image file must be
    /// attached"), so the extra references a card carries cannot travel — and the prompt is
    /// corrected to say so. A prompt describing images the model never received produces
    /// confident nonsense.
    /// </summary>
    [Fact]
    public async Task A_mai_edit_sends_exactly_one_reference_and_says_so_in_the_prompt()
    {
        var handler = new CapturingImageHandler();
        var provider = NewFoundryProvider(handler, deployment: "MAI-Image-2.5-Pro");
        var png = SolidPng(8, 8, SKColors.Red);

        await provider.GenerateAsync(
            Guid.NewGuid(), "a product hero", "1:1", null,
            [
                new ImageReference(Guid.NewGuid(), "chosen.png", "image/png", png, "product"),
                new ImageReference(Guid.NewGuid(), "dropped.png", "image/png", png, "face"),
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal("https://foundry.services.ai.azure.com/mai/v1/images/edits", handler.RequestUri);
        // One `image` part, not the `image[]` array the gpt-image edits endpoint takes.
        // (.NET only quotes a multipart name that needs it, so this one is bare.)
        Assert.Contains("name=image;", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("image[]", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("chosen.png", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dropped.png", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("Exactly one reference image is attached", handler.LastBody, StringComparison.Ordinal);
        // No size/n/input_fidelity on this surface.
        Assert.DoesNotContain("input_fidelity", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"size\"", handler.LastBody, StringComparison.Ordinal);
    }

    /// <summary>A single reference needs no correction — the prompt is left exactly as composed.</summary>
    [Fact]
    public async Task A_mai_edit_with_one_reference_leaves_the_prompt_alone()
    {
        var handler = new CapturingImageHandler();
        var provider = NewFoundryProvider(handler, deployment: "MAI-Image-2.5-Pro");

        await provider.GenerateAsync(
            Guid.NewGuid(), "a product hero", "1:1", null,
            [new ImageReference(Guid.NewGuid(), "only.png", "image/png", SolidPng(8, 8, SKColors.Red), "product")],
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Exactly one reference image is attached", handler.LastBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// A dialect guess is not load-bearing: a 404 on one surface retries on the other and the
    /// answer is remembered, so a renamed deployment (or Microsoft moving a path) costs one
    /// wasted round trip instead of an outage.
    /// </summary>
    [Fact]
    public async Task A_404_on_the_assumed_dialect_retries_on_the_other_one_and_remembers()
    {
        var handler = new CapturingImageHandler
        {
            FirstResponse = (HttpStatusCode.NotFound,
                """{"error":{"code":"not_found","message":"Requested path is not found"}}"""),
        };
        var capabilities = new ImageModelCapabilities();
        // A MAI model under a name the seeded rule cannot recognise.
        var provider = NewFoundryProvider(handler, deployment: "house-image-model", capabilities);

        var result = await provider.GenerateAsync(
            Guid.NewGuid(), "a lighthouse", "1:1", null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(result);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("/mai/v1/images/generations", handler.RequestUri, StringComparison.Ordinal);
        Assert.Equal(ImageDialect.Mai, capabilities.DialectFor("house-image-model"));
    }

    [Theory]
    // 16:9 sits exactly on the 1 MP ceiling once the short edge is at its 768 floor.
    [InlineData("16:9", 1365, 768)]
    [InlineData("9:16", 768, 1365)]
    [InlineData("3:2", 1254, 836)]
    [InlineData("2:3", 836, 1254)]
    [InlineData("1:1", 1024, 1024)]
    [InlineData("landscape", 1365, 768)]
    public void Mai_frames_respect_the_768_floor_and_the_one_megapixel_ceiling(
        string aspect, int expectedWidth, int expectedHeight)
    {
        var (width, height) = MaiImages.FrameFor(aspect);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
        Assert.True(width >= MaiImages.MinEdge && height >= MaiImages.MinEdge);
        Assert.True(width * height <= MaiImages.MaxPixels, $"{width}x{height} exceeds the pixel budget");
    }

    /// <summary>The MAI hostname is derived from the configured one — the same resource under a
    /// different name — so there is no second endpoint setting to keep in sync.</summary>
    [Theory]
    [InlineData("https://castmill.openai.azure.com/", "https://castmill.services.ai.azure.com")]
    [InlineData("https://castmill.cognitiveservices.azure.com", "https://castmill.services.ai.azure.com")]
    [InlineData("https://castmill.services.ai.azure.com/", "https://castmill.services.ai.azure.com")]
    [InlineData("https://ai.internal.example/gateway", "https://ai.internal.example")]
    public void The_mai_hostname_is_derived_from_the_configured_endpoint(string configured, string expected) =>
        Assert.Equal(expected, MaiImages.HostFor(configured));

    /// <summary>A rate-limited image deployment is a real condition (MAI quota is single-digit
    /// requests per minute), so it reads as one — not as a bug in the app.</summary>
    [Fact]
    public async Task A_rate_limited_deployment_says_so_in_words()
    {
        var handler = new CapturingImageHandler
        {
            FirstResponse = ((HttpStatusCode)429, """{"error":{"code":"429","message":"Requests to the model exceeded the quota."}}"""),
            RepeatFirstResponse = true,
        };
        var provider = NewFoundryProvider(handler, deployment: "MAI-Image-2.5-Pro");

        var ex = await Assert.ThrowsAsync<ImageProviderException>(() => provider.GenerateAsync(
            Guid.NewGuid(), "a lighthouse", "1:1", null, TestContext.Current.CancellationToken));

        Assert.Contains("rate limited", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fewer takes", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Nano Banana: prompt and references are parts of one content turn, the ratio is
    /// a generation-config field, and the image comes back as inline base64.</summary>
    [Fact]
    public async Task Nano_banana_posts_generate_content_with_inline_reference_parts()
    {
        var png = SolidPng(12, 12, SKColors.Green);
        var handler = new GeminiHandler(png);
        var provider = new GeminiImageProvider(
            "nano-banana",
            new AiOptions.ImageProviderOptions
            {
                Enabled = true,
                Kind = "gemini",
                Endpoint = "https://generativelanguage.example/v1beta",
                Model = "gemini-2.5-flash-image",
                Credential = SecretKind.NanoBananaKey,
            },
            new SingleHttpClientFactory(new HttpClient(handler)),
            new StubSecrets("AIza-test-key"),
            NullLogger.Instance);

        var result = await provider.GenerateAsync(
            Guid.NewGuid(), "a lighthouse", "16:9", null,
            [new ImageReference(Guid.NewGuid(), "face.png", "image/png", png, "face")],
            TestContext.Current.CancellationToken);

        Assert.Equal(png, result);
        Assert.Contains("models/gemini-2.5-flash-image:generateContent", handler.RequestUri, StringComparison.Ordinal);
        Assert.Equal("AIza-test-key", handler.ApiKeyHeader);
        Assert.Contains("\"aspectRatio\":\"16:9\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("inline_data", handler.Body, StringComparison.Ordinal);
        // The key belongs in a header — a key in a query string reaches request logs.
        Assert.DoesNotContain("AIza-test-key", handler.RequestUri, StringComparison.Ordinal);
    }

    /// <summary>A provider with no stored key is not ready, and says which credential to fill
    /// in. It must never be a failed click (G3).</summary>
    [Fact]
    public async Task A_provider_without_its_key_is_not_ready_and_names_the_credential()
    {
        var provider = new GeminiImageProvider(
            "nano-banana",
            new AiOptions.ImageProviderOptions
            {
                Enabled = true,
                Kind = "gemini",
                Endpoint = "https://generativelanguage.example/v1beta",
                Model = "gemini-2.5-flash-image",
                Credential = SecretKind.NanoBananaKey,
            },
            new SingleHttpClientFactory(new HttpClient()),
            new StubSecrets(null),
            NullLogger.Instance);

        var status = await provider.StatusAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.False(status.Ready);
        Assert.Contains("NanoBananaKey", status.Reason!, StringComparison.Ordinal);
        Assert.True(status.SupportsReferenceImages);
    }

    /// <summary>The two shipped alternates are selectable out of the box, but inert until a
    /// key is stored: the credential is the gate, not a config flag.</summary>
    [Fact]
    public void Nano_banana_and_gpt_image_ship_as_named_providers_with_their_own_credential()
    {
        var merged = AiOptions.MergeImageProviders(new Dictionary<string, AiOptions.ImageProviderOptions>());

        var nano = merged["nano-banana"];
        Assert.True(nano.Enabled);
        Assert.Equal("gemini", nano.Kind);
        Assert.Equal(SecretKind.NanoBananaKey, nano.Credential);

        var gpt = merged["gpt-image"];
        Assert.True(gpt.Enabled);
        Assert.Equal("openai", gpt.Kind);
        Assert.Equal(SecretKind.OpenAiImageKey, gpt.Credential);
    }

    /// <summary>Config overrides one field without blanking the rest — pinning a model must
    /// not wipe the built-in endpoint or silently disable the provider.</summary>
    [Fact]
    public void Config_overrides_one_field_of_a_built_in_provider_without_erasing_the_others()
    {
        var merged = AiOptions.MergeImageProviders(new Dictionary<string, AiOptions.ImageProviderOptions>
        {
            ["nano-banana"] = new() { Model = "gemini-3-pro-image-preview" },
            ["invented"] = new() { Endpoint = "https://images.example/v1" },
        });

        var nano = merged["nano-banana"];
        Assert.Equal("gemini-3-pro-image-preview", nano.Model);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta", nano.Endpoint);
        Assert.True(nano.Enabled);
        Assert.Equal(SecretKind.NanoBananaKey, nano.Credential);
        // An invented provider still has to opt in (ADR-015).
        Assert.False(merged["invented"].Enabled);
    }

    /// <summary>An explicit Enabled=false removes even a built-in provider.</summary>
    [Fact]
    public void Config_can_switch_a_built_in_provider_off()
    {
        var merged = AiOptions.MergeImageProviders(new Dictionary<string, AiOptions.ImageProviderOptions>
        {
            ["gpt-image"] = new() { Enabled = false },
        });

        Assert.False(merged["gpt-image"].Enabled);
        Assert.True(merged["nano-banana"].Enabled);
    }

    private static FoundryImageProvider NewFoundryProvider(
        HttpMessageHandler handler, string deployment, IImageModelCapabilities? capabilities = null) =>
        new(new StaticFoundryTarget(deployment),
            new SingleHttpClientFactory(new HttpClient(handler)),
            capabilities ?? new ImageModelCapabilities(),
            NullLogger<FoundryImageProvider>.Instance,
            Options.Create(new AiOptions { ImageApiVersion = "2025-04-01-preview" }));

    private sealed class StaticFoundryTarget(string deployment) : IFoundryClientFactory
    {
        private static readonly FoundryCredentials Credentials =
            new("https://foundry.openai.azure.com", "secret", "test");
        public Task<FoundryCredentials?> ResolveCredentialsAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<FoundryCredentials?>(Credentials);
        public string? ResolveDeployment(string modelAlias) => deployment;
        public Task<FoundryTarget?> ResolveTargetAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<FoundryTarget?>(new FoundryTarget(Credentials, deployment));
        public Task<IChatClient> CreateChatClientAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubSecrets(string? value) : IUserSecretsService
    {
        public Task SetAsync(Guid userId, SecretKind kind, string v, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetAsync(Guid userId, SecretKind kind, CancellationToken ct) => Task.FromResult(value);
        public Task<bool> RemoveAsync(Guid userId, SecretKind kind, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyDictionary<SecretKind, DateTimeOffset>> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<SecretKind, DateTimeOffset>>(
                new Dictionary<SecretKind, DateTimeOffset>());
    }

    /// <summary>
    /// Records every attempt — the repair path needs more than "the last one" — and can be
    /// told to answer with a provider error.
    /// </summary>
    private sealed class CapturingImageHandler : HttpMessageHandler
    {
        public string RequestUri { get; private set; } = string.Empty;
        public List<string> Bodies { get; } = [];
        public string LastBody => Bodies.Count == 0 ? string.Empty : Bodies[^1];
        public string? ApiKey { get; private set; }
        public (HttpStatusCode Status, string Body)? FirstResponse { get; init; }
        /// <summary>Answer EVERY attempt with <see cref="FirstResponse"/>, not just the first.</summary>
        public bool RepeatFirstResponse { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri!.ToString();
            ApiKey = request.Headers.GetValues("api-key").Single();
            Bodies.Add(System.Text.Encoding.Latin1.GetString(
                await request.Content!.ReadAsByteArrayAsync(cancellationToken)));

            if (FirstResponse is { } failure && (RepeatFirstResponse || Bodies.Count == 1))
            {
                return new HttpResponseMessage(failure.Status)
                {
                    Content = new StringContent(failure.Body, System.Text.Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    data = new[] { new { b64_json = Convert.ToBase64String(SolidPng(16, 16, SKColors.Blue)) } },
                }),
            };
        }
    }

    /// <summary>Answers the generate call with a URL, then serves the bytes at that URL.</summary>
    private sealed class UrlResponseHandler(byte[] image) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        data = new[] { new { url = "https://images.example/generated.png" } },
                    }),
                });
    }

    private sealed class GeminiHandler(byte[] image) : HttpMessageHandler
    {
        public string RequestUri { get; private set; } = string.Empty;
        public string Body { get; private set; } = string.Empty;
        public string? ApiKeyHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri!.ToString();
            ApiKeyHeader = request.Headers.TryGetValues("x-goog-api-key", out var values)
                ? values.Single() : null;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    candidates = new[]
                    {
                        new
                        {
                            content = new
                            {
                                parts = new[]
                                {
                                    new
                                    {
                                        inlineData = new
                                        {
                                            mimeType = "image/png",
                                            data = Convert.ToBase64String(image),
                                        },
                                    },
                                },
                            },
                        },
                    },
                }),
            };
        }
    }
}
