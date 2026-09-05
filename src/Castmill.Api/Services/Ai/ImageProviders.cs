using System.Collections.Concurrent;
using System.Net.Http.Headers;
using SkiaSharp;
using System.Text.Json;
using Castmill.Api.Services.Images;
using Castmill.Api.Services.Secrets;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Services.Ai;

public sealed record ImageProviderStatus(
    string Name, bool Ready, string? Reason, bool SupportsReferenceImages = false);

/// <summary>
/// A provider refused or failed a render, with a message that is safe — and useful — to
/// show the producer. Every provider failure lands here rather than as a bare
/// InvalidOperationException: the studio used to display only the exception TYPE, which
/// meant a deterministic 400 ("this model does not support that parameter") looked
/// identical to a transient network fault and was re-diagnosed from scratch every time.
/// </summary>
public sealed class ImageProviderException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

/// <summary>
/// Image-generation seam (ADR-015). Foundry is the default; Nano Banana (Gemini) and
/// OpenAI's gpt-image are first-class alternates, each with its own per-user credential.
/// Text generation has no such seam — it is Foundry-only.
/// </summary>
public interface IImageProvider
{
    string Name { get; }
    Task<ImageProviderStatus> StatusAsync(Guid userId, CancellationToken ct);
    /// <summary>Returns raw encoded image bytes (PNG/JPEG/WebP) — the caller crops and re-encodes.
    /// <paramref name="aspectRatio"/> is either a ratio ("16:9") or the slot's exact size
    /// ("1280x720"); a provider that can paint exact sizes does, the others map it to their
    /// nearest native frame (ADR-055).</summary>
    Task<byte[]> GenerateAsync(Guid userId, string prompt, string aspectRatio, string? modelAlias, CancellationToken ct);

    /// <summary>
    /// Region edit (ADR-055): repaint the masked part of <paramref name="image"/> per the
    /// instruction and leave the rest alone. <paramref name="maskPng"/> is white/opaque where
    /// the edit applies. Providers without a mask parameter receive the mask as a second
    /// image plus a description of the region. Default: not supported.
    /// </summary>
    Task<byte[]> EditAsync(
        Guid userId, string instruction, byte[] image, byte[] maskPng, string? modelAlias, CancellationToken ct) =>
        throw new ImageProviderException($"Image provider '{Name}' does not support region edits.");

    /// <summary>
    /// The frame this provider will actually paint for a slot of this size — what the crop
    /// pass receives, and what the safe-margin rules are written against. Default: the
    /// gpt-image fixed size set.
    /// </summary>
    Task<(int Width, int Height)> FrameForAsync(Guid userId, int targetWidth, int targetHeight, string? modelAlias, CancellationToken ct) =>
        Task.FromResult(ImageAspect.FixedFrame(ImageAspect.Describe(targetWidth, targetHeight)));

    Task<byte[]> GenerateAsync(
        Guid userId, string prompt, string aspectRatio, string? modelAlias,
        IReadOnlyList<ImageReference> references, CancellationToken ct) =>
        GenerateAsync(userId, prompt, aspectRatio, modelAlias, ct);
}

public interface IImageProviderRegistry
{
    /// <summary>
    /// Routes on the alias: a value naming a configured provider selects it,
    /// anything else is a Foundry model alias.
    /// </summary>
    IImageProvider Resolve(string? modelAliasOrProvider);
    Task<IReadOnlyList<ImageProviderStatus>> StatusAsync(Guid userId, CancellationToken ct);
}

public sealed class ImageProviderRegistry(
    FoundryImageProvider foundry,
    IEnumerable<ConfiguredImageProvider> external) : IImageProviderRegistry
{
    private readonly Dictionary<string, IImageProvider> _byName =
        external.Where(p => p.IsEnabled).ToDictionary<ConfiguredImageProvider, string, IImageProvider>(
            p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

    public IImageProvider Resolve(string? modelAliasOrProvider) =>
        modelAliasOrProvider is not null && _byName.TryGetValue(modelAliasOrProvider, out var provider)
            ? provider
            : foundry;

    public async Task<IReadOnlyList<ImageProviderStatus>> StatusAsync(Guid userId, CancellationToken ct)
    {
        var statuses = new List<ImageProviderStatus> { await foundry.StatusAsync(userId, ct) };
        foreach (var provider in _byName.Values)
        {
            statuses.Add(await provider.StatusAsync(userId, ct));
        }
        return statuses;
    }
}

/// <summary>
/// Which optional request parameters a given image model actually accepts.
///
/// This exists because of a real, repeated outage: every reference-image render sent
/// <c>input_fidelity=high</c>, which gpt-image-1 supports and <b>gpt-image-2 rejects with a
/// 400</b> — so switching the deployment silently broke 100% of renders that attach a brand
/// or product reference. Seeded knowledge keeps the common case from paying a wasted round
/// trip; a 400 that names an unsupported parameter is recorded here so the SAME request is
/// retried without it once and never sent again for that model.
/// </summary>
public interface IImageModelCapabilities
{
    bool Supports(string model, string parameter);
    /// <summary>Records a provider's refusal of a parameter. Process-lifetime memory.</summary>
    void MarkUnsupported(string model, string parameter);

    /// <summary>Which wire dialect a Foundry deployment speaks (ADR-038).</summary>
    ImageDialect DialectFor(string model);
    /// <summary>Records the dialect a deployment actually answered on. Process-lifetime memory.</summary>
    void MarkDialect(string model, ImageDialect dialect);
}

/// <summary>
/// Foundry serves image models through two unrelated HTTP surfaces, and a deployment answers
/// on exactly one of them:
///
/// <list type="bullet">
/// <item><b>AzureOpenAI</b> — <c>{endpoint}/openai/deployments/{deployment}/images/generations|edits?api-version=…</c>,
///   sizes as <c>"1536x1024"</c>, many reference images.</item>
/// <item><b>Mai</b> — Microsoft's own image family (MAI-Image-*) on
///   <c>{resource}.services.ai.azure.com/mai/v1/images/generations|edits</c>: no api-version,
///   <c>width</c>/<c>height</c> integers instead of <c>size</c>, and <b>exactly one</b>
///   reference image. The AzureOpenAI paths return 404 for these deployments even though the
///   deployment is listed and healthy, which is precisely how "image-alt" looked broken.</item>
/// </list>
/// </summary>
public enum ImageDialect
{
    AzureOpenAI,
    Mai,
}

public sealed class ImageModelCapabilities : IImageModelCapabilities
{
    /// <summary>Fidelity-preserving reference input. gpt-image-1 only, at time of writing.</summary>
    public const string InputFidelity = "input_fidelity";

    private readonly ConcurrentDictionary<string, bool> _unsupported = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ImageDialect> _dialects = new(StringComparer.OrdinalIgnoreCase);

    public bool Supports(string model, string parameter) =>
        !_unsupported.ContainsKey(Key(model, parameter)) && SupportedByDefault(model, parameter);

    public void MarkUnsupported(string model, string parameter) =>
        _unsupported[Key(model, parameter)] = true;

    public ImageDialect DialectFor(string model) =>
        _dialects.TryGetValue(model, out var learned) ? learned : DialectByDefault(model);

    public void MarkDialect(string model, ImageDialect dialect) => _dialects[model] = dialect;

    /// <summary>
    /// Microsoft's MAI image family speaks its own surface. Matched on the deployment name
    /// because that name IS the model family here (a deployment of MAI-Image-2.5-Pro is
    /// conventionally named for it), and a wrong guess self-corrects: a 404 on one dialect
    /// retries on the other and the answer is remembered.
    /// </summary>
    private static ImageDialect DialectByDefault(string model) =>
        model.StartsWith("MAI-", StringComparison.OrdinalIgnoreCase)
        || model.Contains("mai-image", StringComparison.OrdinalIgnoreCase)
            ? ImageDialect.Mai
            : ImageDialect.AzureOpenAI;

    /// <summary>
    /// Seeded expectations. Deliberately an allow-list per parameter rather than a
    /// deny-list: a model we have never heard of gets the plain request that every
    /// image API accepts, not an optional extra that might 400 it.
    /// </summary>
    private static bool SupportedByDefault(string model, string parameter) => parameter switch
    {
        InputFidelity => model.Contains("gpt-image-1", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    private static string Key(string model, string parameter) => $"{model}{parameter}";
}

/// <summary>
/// Aspect vocabulary shared by every provider (ADR-055). A request names either a ratio
/// ("16:9", "landscape") or the slot's exact size ("1280x720"); these helpers translate
/// both into what each provider can actually paint.
/// </summary>
public static class ImageAspect
{
    /// <summary>"1280x720" for an exact target — providers that can, paint it.</summary>
    public static string Describe(int width, int height) => $"{width}x{height}";

    /// <summary>Exact pixel size when the descriptor is one, else null.</summary>
    public static (int Width, int Height)? ExactSize(string aspectOrSize)
    {
        var parts = aspectOrSize.Trim().Split('x', 'X', '×');
        return parts.Length == 2
            && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)
            && w > 0 && h > 0
                ? (w, h)
                : null;
    }

    /// <summary>The ratio class the fixed-size providers understand: 16:9, 9:16 or 1:1.</summary>
    public static string Bucket(string aspectOrSize)
    {
        if (ExactSize(aspectOrSize) is { } size)
        {
            return Bucket(size.Width, size.Height);
        }
        return aspectOrSize.Trim() switch
        {
            "16:9" or "3:2" or "landscape" or "4:3" or "21:9" => "16:9",
            "9:16" or "2:3" or "portrait" or "3:4" => "9:16",
            _ => "1:1",
        };
    }

    public static string Bucket(int width, int height) => ((float)width / height) switch
    {
        > 1.15f => "16:9",
        < 0.87f => "9:16",
        _ => "1:1",
    };

    /// <summary>The gpt-image family's fixed frames.</summary>
    public static (int Width, int Height) FixedFrame(string aspectOrSize) => Bucket(aspectOrSize) switch
    {
        "16:9" => (1536, 1024),
        "9:16" => (1024, 1536),
        _ => (1024, 1024),
    };

    /// <summary>Width/height as a ratio pair, from either form.</summary>
    public static (int Width, int Height) Ratio(string aspectOrSize)
    {
        if (ExactSize(aspectOrSize) is { } size)
        {
            return (size.Width, size.Height);
        }
        return aspectOrSize.Trim() switch
        {
            "landscape" => (16, 9),
            "portrait" => (9, 16),
            var value when value.Split(':') is [var w, var h]
                && int.TryParse(w, out var parsedW) && int.TryParse(h, out var parsedH)
                && parsedW > 0 && parsedH > 0 => (parsedW, parsedH),
            _ => (1, 1),
        };
    }
}

/// <summary>
/// Shared plumbing for the OpenAI-shaped image APIs (Azure Foundry and OpenAI direct):
/// error parsing that produces a producer-facing sentence, and the one-shot
/// "drop the parameter the model just rejected and retry" repair.
/// </summary>
internal static class OpenAiShapedImages
{
    /// <summary>A provider error, reduced to the parts that are safe to show.</summary>
    internal sealed record ProviderError(int Status, string? Code, string? Param, string Message);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A JSON body with a known length. <c>JsonContent.Create</c> serialises while sending, so
    /// the request goes out chunked with no Content-Length — which the MAI gateway rejects
    /// outright ("Content-Length header is required to make request"). Every image request is
    /// small and already fully in memory, so buffering costs nothing and removes a dependency
    /// on each gateway's tolerance for chunked uploads.
    /// </summary>
    internal static ByteArrayContent JsonBody(object body)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    /// <summary>
    /// Sends a request built by <paramref name="build"/>, and if the provider rejects one
    /// optional parameter by name, rebuilds without it and sends once more. The factory
    /// shape is required, not incidental: an HttpRequestMessage (and its multipart body)
    /// cannot be sent twice.
    /// </summary>
    internal static async Task<byte[]> SendWithParameterRepairAsync(
        HttpClient client,
        string providerLabel,
        string model,
        IImageModelCapabilities capabilities,
        ILogger logger,
        Func<ISet<string>, HttpRequestMessage> build,
        CancellationToken ct,
        Func<bool>? tryOtherDialect = null)
    {
        var omit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var switchedDialect = false;
        for (var attempt = 0; ; attempt++)
        {
            using var request = build(omit);
            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                return await ReadImageAsync(client, providerLabel, body, ct);
            }

            var error = Parse((int)response.StatusCode, body);
            logger.LogError(
                "{Provider} image request failed: HTTP {Status} code={Code} param={Param} message={Message}",
                providerLabel, error.Status, error.Code ?? "-", error.Param ?? "-", error.Message);

            if (attempt < 2 && UnsupportedParameter(error) is { } parameter && omit.Add(parameter))
            {
                // Self-healing: remember it for the process, retry immediately without it.
                capabilities.MarkUnsupported(model, parameter);
                logger.LogWarning(
                    "{Provider} model {Model} rejected '{Parameter}' — retrying without it and "
                    + "omitting it for the rest of this process",
                    providerLabel, model, parameter);
                continue;
            }

            // A 404 means "this deployment does not answer on this surface", not "no such
            // model" — the two Foundry image dialects have disjoint paths. Try the other one
            // once; the winner is remembered so no later render pays for the discovery.
            if (!switchedDialect && error.Status == 404 && tryOtherDialect?.Invoke() == true)
            {
                switchedDialect = true;
                logger.LogWarning(
                    "{Provider} returned 404 on its assumed image dialect — retrying {Model} on the other one",
                    providerLabel, model);
                continue;
            }

            throw ToException(providerLabel, error);
        }
    }

    /// <summary>
    /// data[0].b64_json, or data[0].url when a provider answers with a link instead
    /// (dall-e-style responses). Following the link matters: the old code called
    /// <c>ImageBytes.ToArray()</c> unconditionally and a URL response surfaced as
    /// "model returned bytes that are not a decodable image".
    /// </summary>
    private static async Task<byte[]> ReadImageAsync(
        HttpClient client, string providerLabel, string body, CancellationToken ct)
    {
        JsonElement first;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                throw new ImageProviderException(
                    $"{providerLabel} answered with no image data. The prompt may have been "
                    + "filtered — try rewording it.");
            }
            first = data[0].Clone();
        }
        catch (JsonException ex)
        {
            throw new ImageProviderException($"{providerLabel} returned a response that isn't JSON.", ex);
        }

        if (first.TryGetProperty("b64_json", out var b64) && b64.GetString() is { Length: > 0 } encoded)
        {
            try
            {
                return Convert.FromBase64String(encoded);
            }
            catch (FormatException ex)
            {
                throw new ImageProviderException($"{providerLabel} returned image data that isn't base64.", ex);
            }
        }

        if (first.TryGetProperty("url", out var urlValue) && urlValue.GetString() is { Length: > 0 } url)
        {
            using var download = await client.GetAsync(url, ct);
            return download.IsSuccessStatusCode
                ? await download.Content.ReadAsByteArrayAsync(ct)
                : throw new ImageProviderException(
                    $"{providerLabel} returned an image URL that could not be downloaded "
                    + $"(HTTP {(int)download.StatusCode}).");
        }

        throw new ImageProviderException(
            $"{providerLabel} returned a result with neither image bytes nor a URL.");
    }

    /// <summary>Pulls error.message/code/param out of the OpenAI error envelope.</summary>
    internal static ProviderError Parse(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var message = error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : error.TryGetProperty("message", out var m) ? m.GetString() : null;
                return new ProviderError(
                    status,
                    error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c)
                        ? c.ValueKind == JsonValueKind.String ? c.GetString() : c.ToString()
                        : null,
                    error.ValueKind == JsonValueKind.Object && error.TryGetProperty("param", out var p)
                        ? p.GetString() : null,
                    string.IsNullOrWhiteSpace(message) ? "no message" : message);
            }
        }
        catch (JsonException)
        {
            // Falls through to the status-only error below.
        }

        // No parseable envelope: the raw body is NOT surfaced to the client — a provider
        // error can quote the request it received, and that request carries credentials.
        return new ProviderError(status, null, null, "unreadable error body (see server log)");
    }

    /// <summary>
    /// The parameter a provider says it will not accept — the repair hook. Recognises both
    /// the "does not support the 'x' parameter" family (Azure/OpenAI images) and the
    /// generic unknown/unsupported-parameter codes.
    /// </summary>
    internal static string? UnsupportedParameter(ProviderError error)
    {
        if (error.Status != 400)
        {
            return null;
        }

        var named = FromMessage(error.Message);
        if (named is not null)
        {
            return named;
        }

        var code = error.Code ?? string.Empty;
        var unsupportedCode =
            code.Equals("unknown_parameter", StringComparison.OrdinalIgnoreCase)
            || code.Equals("unsupported_parameter", StringComparison.OrdinalIgnoreCase)
            || (code.StartsWith("invalid_", StringComparison.OrdinalIgnoreCase)
                && code.EndsWith("_model", StringComparison.OrdinalIgnoreCase));
        return unsupportedCode && !string.IsNullOrWhiteSpace(error.Param) ? error.Param : null;
    }

    private static string? FromMessage(string message)
    {
        foreach (var marker in (string[])["does not support the '", "unknown parameter: '", "unsupported parameter: '"])
        {
            var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                continue;
            }
            start += marker.Length;
            var end = message.IndexOf('\'', start);
            if (end > start)
            {
                return message[start..end];
            }
        }
        return null;
    }

    internal static InvalidOperationException ToException(string providerLabel, ProviderError error)
    {
        // Moderation refusals get producer-facing advice: this is the one failure the user can
        // actually act on. Every vendor spells it differently — Azure OpenAI says
        // "moderation_blocked", the MAI surface says "content_safety_violation / violated
        // mainline safety policies" — so match the family, not one string.
        if (IsSafetyRefusal(error))
        {
            return new ImageModerationException(
                "The provider's safety system declined this render, and it judges the reference "
                + "images as well as the prompt. A photograph of a real person is the usual "
                + "trigger: swap that reference out (a product or background asset is normally "
                + "fine), or generate without references. Azure OpenAI deployments can also be "
                + $"granted modified content filters; MAI deployments cannot. Provider said: "
                + $"{Truncate(error.Message, 160)}");
        }

        // MAI image deployments are quota-limited in single-digit requests per MINUTE, so a
        // multi-take batch hits this legitimately. Say that, rather than leaving "429" to look
        // like a bug in the app.
        if (error.Status == 429)
        {
            return new ImageProviderException(
                $"{providerLabel} is rate limited right now (429). Image deployments can allow only "
                + "a few requests per minute — wait a moment, generate fewer takes at once, or "
                + $"raise the deployment's quota. Provider said: {Truncate(error.Message, 200)}");
        }

        return new ImageProviderException(
            $"{providerLabel} refused this render (HTTP {error.Status}"
            + (string.IsNullOrWhiteSpace(error.Code) ? ")" : $", {error.Code})")
            + $": {Truncate(error.Message, 400)}");
    }

    /// <summary>
    /// A content-safety refusal in any of the spellings the image providers use. Matched on the
    /// error code first (structured, stable) and on distinctive message fragments second.
    /// </summary>
    private static bool IsSafetyRefusal(ProviderError error)
    {
        var code = error.Code ?? string.Empty;
        foreach (var marker in (string[])["moderation", "content_safety", "content_filter", "responsible_ai", "jailbreak"])
        {
            if (code.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        foreach (var marker in (string[])["moderation_blocked", "safety system", "safety polic", "content management polic"])
        {
            if (error.Message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    internal static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max].TrimEnd() + "…";

    /// <summary>
    /// Multipart body for an edit of ONE image with an optional alpha mask. The image's own
    /// size is requested back so the take stays the slot's size; the mask, when present, is
    /// converted to the alpha convention and fitted to the image.
    /// </summary>
    internal static MultipartFormDataContent MaskedEditForm(
        string prompt, string model, byte[] image, byte[]? maskPng, ISet<string> omit, IImageModelCapabilities capabilities)
    {
        using var decoded = Castmill.Api.Services.Images.ImageReferenceResolver.TryDecode(image)
            ?? throw new ImageProviderException("The take to edit is not a decodable image.");
        var size = $"{ImageAspect.FixedFrame(ImageAspect.Describe(decoded.Width, decoded.Height)).Width}x{ImageAspect.FixedFrame(ImageAspect.Describe(decoded.Width, decoded.Height)).Height}";
        var form = new MultipartFormDataContent
        {
            { new StringContent(prompt), "prompt" },
            { new StringContent(model), "model" },
            { new StringContent("1"), "n" },
        };
        if (maskPng is not null)
        {
            form.Add(new StringContent(size), "size");
        }
        if (!omit.Contains(ImageModelCapabilities.InputFidelity)
            && capabilities.Supports(model, ImageModelCapabilities.InputFidelity))
        {
            form.Add(new StringContent("high"), ImageModelCapabilities.InputFidelity);
        }
        var imageContent = new ByteArrayContent(image);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
        form.Add(imageContent, maskPng is null ? "image" : "image[]", "take.webp");
        if (maskPng is not null)
        {
            var mask = new ByteArrayContent(RegionEdits.ToAlphaMask(maskPng, decoded.Width, decoded.Height));
            mask.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(mask, "mask", "mask.png");
        }
        return form;
    }

    /// <summary>Fixed size set the gpt-image family accepts; the crop pass fixes the rest.</summary>
    internal static string SizeFor(string aspectRatio)
    {
        var (width, height) = ImageAspect.FixedFrame(aspectRatio);
        return $"{width}x{height}";
    }
}

/// <summary>Prompt and mask helpers for region edits (ADR-055), shared by every provider.</summary>
internal static class RegionEdits
{
    internal static string EditPrompt(string instruction) =>
        $"{instruction.Trim()}\nEdit ONLY the masked region. Everything outside the mask must stay "
        + "pixel-identical: same subject, same lighting, same framing, same colours.";

    /// <summary>
    /// For providers that take no mask: the masked area's bounding box, as fractions of the
    /// frame in plain words, so the model knows where the change belongs.
    /// </summary>
    internal static string DescribeRegion(string instruction, byte[] maskPng)
    {
        var box = MaskBounds(maskPng);
        var where = box is null
            ? "the region the producer selected"
            : $"the region spanning {Pct(box.Value.Left)}–{Pct(box.Value.Right)} of the width and "
              + $"{Pct(box.Value.Top)}–{Pct(box.Value.Bottom)} of the height (0% = left/top)";
        return $"{instruction.Trim()}\nApply this change to {where} only, and keep everything "
            + "outside it exactly as it is in the attached image.";
    }

    private static string Pct(float value) => $"{Math.Round(value * 100)}%";

    /// <summary>Bounding box (fractions) of the opaque/white pixels in the mask; null when empty.</summary>
    internal static (float Left, float Top, float Right, float Bottom)? MaskBounds(byte[] maskPng)
    {
        using var mask = Castmill.Api.Services.Images.ImageReferenceResolver.TryDecode(maskPng);
        if (mask is null)
        {
            return null;
        }
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < mask.Height; y++)
        {
            for (var x = 0; x < mask.Width; x++)
            {
                var c = mask.GetPixel(x, y);
                if (c.Alpha > 127 && (c.Red + c.Green + c.Blue) > 3 * 127)
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }
        }
        return maxX < 0
            ? null
            : ((float)minX / mask.Width, (float)minY / mask.Height, (float)(maxX + 1) / mask.Width, (float)(maxY + 1) / mask.Height);
    }

    /// <summary>
    /// OpenAI-shaped edit masks are read from ALPHA: transparent = repaint. The editor draws
    /// white-on-transparent, so white pixels become transparent and everything else opaque.
    /// The mask is also resized to the image's own dimensions, which the API requires.
    /// </summary>
    internal static byte[] ToAlphaMask(byte[] maskPng, int width, int height)
    {
        using var mask = Castmill.Api.Services.Images.ImageReferenceResolver.TryDecode(maskPng)
            ?? throw new ImageProviderException("The mask is not a decodable PNG.");
        using var fitted = mask.Width == width && mask.Height == height
            ? mask.Copy()
            : mask.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Nearest))
              ?? throw new ImageProviderException("The mask could not be resized.");
        using var output = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var c = fitted.GetPixel(x, y);
                var edit = c.Alpha > 127 && (c.Red + c.Green + c.Blue) > 3 * 127;
                output.SetPixel(x, y, edit ? SKColors.Transparent : SKColors.Black);
            }
        }
        using var image = SKImage.FromBitmap(output);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new ImageProviderException("The alpha mask could not be encoded.");
        return encoded.ToArray();
    }
}

/// <summary>
/// The MAI image surface (ADR-038). Kept beside the OpenAI-shaped helper rather than inside it
/// because only the URL and the request body differ — the error envelope
/// (<c>{error:{code,message}}</c>) and the result shape (<c>data[0].b64_json</c>) are identical,
/// so both dialects reuse one parser, one repair pass and one resilient client.
/// </summary>
internal static class MaiImages
{
    /// <summary>Documented floor for either dimension.</summary>
    internal const int MinEdge = 768;
    /// <summary>Documented ceiling on width × height (1024²).</summary>
    internal const int MaxPixels = 1024 * 1024;

    /// <summary>
    /// MAI is addressed on the resource's <c>services.ai.azure.com</c> hostname, which is not
    /// the <c>openai.azure.com</c> name the alias table is configured with. Derived rather than
    /// configured: it is the same resource, and a second endpoint setting to keep in sync is a
    /// second thing to get wrong. Anything unrecognised is passed through untouched so a
    /// sovereign-cloud or custom-domain endpoint fails loudly at the URL it was given.
    /// </summary>
    internal static string HostFor(string endpoint)
    {
        var trimmed = endpoint.TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed;
        }
        var host = uri.Host;
        if (host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            return $"{uri.Scheme}://{host}";
        }
        foreach (var suffix in (string[])[".openai.azure.com", ".cognitiveservices.azure.com"])
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var resource = host[..^suffix.Length];
                return $"{uri.Scheme}://{resource}.services.ai.azure.com";
            }
        }
        return $"{uri.Scheme}://{host}";
    }

    /// <summary>
    /// The largest MAI-legal frame at the requested aspect: both edges ≥ 768 and area ≤ 1 MP.
    /// Unlike the gpt-image family's three fixed sizes, MAI takes arbitrary dimensions — so the
    /// generated frame can match the slot's aspect and the crop pass throws away almost nothing.
    /// </summary>
    internal static (int Width, int Height) FrameFor(string aspectRatio)
    {
        // An exact slot size that satisfies both documented limits is painted as-is: a
        // 1280×720 thumbnail comes back at 1280×720 and the crop pass removes nothing.
        if (ImageAspect.ExactSize(aspectRatio) is { } exact
            && exact.Width >= MinEdge && exact.Height >= MinEdge
            && (long)exact.Width * exact.Height <= MaxPixels)
        {
            return exact;
        }

        var (wRatio, hRatio) = ImageAspect.Ratio(aspectRatio);

        // Spend the whole pixel budget at the requested ratio first — starting from the 768
        // floor instead would hand back 768×768 for a square slot and throw away three
        // quarters of the resolution the model was willing to give.
        var scale = Math.Sqrt((double)MaxPixels / (wRatio * hRatio));
        var width = (int)Math.Floor(wRatio * scale);
        var height = (int)Math.Floor(hRatio * scale);

        // Then honour the floor. For ratios wider than ~1.78 the two constraints collide, and
        // the floor wins: the long edge is clamped to whatever the budget still allows.
        if (width < MinEdge)
        {
            width = MinEdge;
            height = Math.Min(height, MaxPixels / width);
        }
        if (height < MinEdge)
        {
            height = MinEdge;
            width = Math.Min(width, MaxPixels / height);
        }

        // Rounding can only ever push a hair over; give the long edge back a pixel at a time.
        while ((long)width * height > MaxPixels && Math.Max(width, height) > MinEdge)
        {
            if (width >= height)
            {
                width--;
            }
            else
            {
                height--;
            }
        }
        return (Math.Max(width, MinEdge), Math.Max(height, MinEdge));
    }
}

/// <summary>
/// Default provider: the Foundry image deployments behind the alias table.
///
/// Both paths (plain generation and reference-image edits) speak REST through the same
/// resilient named client so they share one timeout policy, one error parser and one
/// parameter-repair pass. The typed SDK was used for plain generation and produced a
/// different, opaque failure shape for the same class of fault.
/// </summary>
public sealed class FoundryImageProvider(
    IFoundryClientFactory clients,
    IHttpClientFactory httpClients,
    IImageModelCapabilities capabilities,
    ILogger<FoundryImageProvider> logger,
    IOptions<AiOptions> options) : IImageProvider
{
    public string Name => "foundry";

    public async Task<ImageProviderStatus> StatusAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            var target = await clients.ResolveTargetAsync(userId, "image", ct);
            return target is null
                ? new ImageProviderStatus(Name, false, "No credentials or no deployment mapped for the 'image' alias.")
                : new ImageProviderStatus(Name, true, null, SupportsReferenceImages: true);
        }
        catch (AiNotConfiguredException ex)
        {
            return new ImageProviderStatus(Name, false, ex.Message);
        }
    }

    public Task<byte[]> GenerateAsync(
        Guid userId, string prompt, string aspectRatio, string? modelAlias, CancellationToken ct) =>
        GenerateAsync(userId, prompt, aspectRatio, modelAlias, [], ct);

    public async Task<(int Width, int Height)> FrameForAsync(
        Guid userId, int targetWidth, int targetHeight, string? modelAlias, CancellationToken ct)
    {
        var alias = string.IsNullOrWhiteSpace(modelAlias) || modelAlias.Equals(Name, StringComparison.OrdinalIgnoreCase)
            ? "image"
            : modelAlias;
        var descriptor = ImageAspect.Describe(targetWidth, targetHeight);
        try
        {
            var target = await clients.ResolveTargetAsync(userId, alias, ct);
            if (target is not null && capabilities.DialectFor(target.Deployment) == ImageDialect.Mai)
            {
                return MaiImages.FrameFor(descriptor);
            }
        }
        catch (AiNotConfiguredException)
        {
            // Unconfigured: report the conservative fixed frame; the render itself will say why.
        }
        return ImageAspect.FixedFrame(descriptor);
    }

    public async Task<byte[]> GenerateAsync(
        Guid userId, string prompt, string aspectRatio, string? modelAlias,
        IReadOnlyList<ImageReference> references, CancellationToken ct)
    {
        // A slot may carry a model alias OR a provider name (the studio's model picker
        // writes the latter — Resolve() routes on it). To this provider its own name
        // means "your default deployment", never an alias-table lookup.
        var alias = string.IsNullOrWhiteSpace(modelAlias) || modelAlias.Equals(Name, StringComparison.OrdinalIgnoreCase)
            ? "image"
            : modelAlias;
        var target = await clients.ResolveTargetAsync(userId, alias, ct)
            ?? throw new AiNotConfiguredException($"No Foundry credentials/deployment for image alias '{alias}'.");

        var deployment = target.Deployment;
        var label = $"Foundry deployment '{deployment}'";
        var dialect = capabilities.DialectFor(deployment);

        return await OpenAiShapedImages.SendWithParameterRepairAsync(
            httpClients.CreateClient("foundry-images"), label, deployment, capabilities, logger,
            omit =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, UrlFor(dialect, target, references.Count));
                request.Headers.TryAddWithoutValidation("api-key", target.Credentials.ApiKey);
                request.Content = dialect == ImageDialect.Mai
                    ? BuildMaiContent(prompt, aspectRatio, deployment, references)
                    : references.Count == 0
                        ? OpenAiShapedImages.JsonBody(new { prompt, size = OpenAiShapedImages.SizeFor(aspectRatio), n = 1 })
                        : BuildEditForm(prompt, OpenAiShapedImages.SizeFor(aspectRatio), deployment, references, omit);
                return request;
            },
            ct,
            tryOtherDialect: () =>
            {
                dialect = dialect == ImageDialect.Mai ? ImageDialect.AzureOpenAI : ImageDialect.Mai;
                capabilities.MarkDialect(deployment, dialect);
                return true;
            });
    }

    public async Task<byte[]> EditAsync(
        Guid userId, string instruction, byte[] image, byte[] maskPng, string? modelAlias, CancellationToken ct)
    {
        var alias = string.IsNullOrWhiteSpace(modelAlias) || modelAlias.Equals(Name, StringComparison.OrdinalIgnoreCase)
            ? "image"
            : modelAlias;
        var target = await clients.ResolveTargetAsync(userId, alias, ct)
            ?? throw new AiNotConfiguredException($"No Foundry credentials/deployment for image alias '{alias}'.");
        var deployment = target.Deployment;
        var label = $"Foundry deployment '{deployment}'";
        var dialect = capabilities.DialectFor(deployment);

        return await OpenAiShapedImages.SendWithParameterRepairAsync(
            httpClients.CreateClient("foundry-images"), label, deployment, capabilities, logger,
            omit =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, UrlFor(dialect, target, referenceCount: 1));
                request.Headers.TryAddWithoutValidation("api-key", target.Credentials.ApiKey);
                request.Content = dialect == ImageDialect.Mai
                    // MAI edits take exactly one image and no mask: the region is described.
                    ? OpenAiShapedImages.MaskedEditForm(RegionEdits.DescribeRegion(instruction, maskPng), deployment, image, null, omit, capabilities)
                    : OpenAiShapedImages.MaskedEditForm(RegionEdits.EditPrompt(instruction), deployment, image, maskPng, omit, capabilities);
                return request;
            },
            ct,
            tryOtherDialect: () =>
            {
                dialect = dialect == ImageDialect.Mai ? ImageDialect.AzureOpenAI : ImageDialect.Mai;
                capabilities.MarkDialect(deployment, dialect);
                return true;
            });
    }

    private string UrlFor(ImageDialect dialect, FoundryTarget target, int referenceCount)
    {
        var route = referenceCount == 0 ? "generations" : "edits";
        if (dialect == ImageDialect.Mai)
        {
            // No api-version on this surface, and the deployment travels in the body.
            return $"{MaiImages.HostFor(target.Credentials.Endpoint)}/mai/v1/images/{route}";
        }
        return $"{target.Credentials.Endpoint.TrimEnd('/')}"
            + $"/openai/deployments/{Uri.EscapeDataString(target.Deployment)}/images/{route}"
            + $"?api-version={Uri.EscapeDataString(options.Value.ImageApiVersion)}";
    }

    /// <summary>
    /// MAI takes <c>width</c>/<c>height</c> for a generation, and for an edit <b>exactly one</b>
    /// image ("Exactly one image file must be attached for edit requests"). The extra references
    /// a card carries cannot be sent, so the prompt is corrected to describe what actually
    /// arrived — a prompt that talks about images the model never received produces confident
    /// nonsense, which is worse than a smaller reference set.
    /// </summary>
    private HttpContent BuildMaiContent(
        string prompt, string aspectRatio, string deployment, IReadOnlyList<ImageReference> references)
    {
        if (references.Count == 0)
        {
            var (width, height) = MaiImages.FrameFor(aspectRatio);
            return OpenAiShapedImages.JsonBody(new { model = deployment, prompt, width, height });
        }

        var reference = references[0];
        var effectivePrompt = prompt;
        if (references.Count > 1)
        {
            logger.LogWarning(
                "{Deployment} accepts one reference image; sending the {Kind} reference and "
                + "dropping {Dropped} other(s)", deployment, reference.Kind, references.Count - 1);
            effectivePrompt = $"{prompt}\nExactly one reference image is attached: the "
                + $"{reference.Kind}. Ignore any instruction about additional reference images.";
        }

        var image = new ByteArrayContent(reference.Bytes);
        image.Headers.ContentType = new MediaTypeHeaderValue(reference.ContentType);
        return new MultipartFormDataContent
        {
            { new StringContent(effectivePrompt), "prompt" },
            { new StringContent(deployment), "model" },
            { image, "image", reference.FileName },
        };
    }

    private MultipartFormDataContent BuildEditForm(
        string prompt, string size, string deployment,
        IReadOnlyList<ImageReference> references, ISet<string> omit)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(prompt), "prompt" },
            { new StringContent(deployment), "model" },
            { new StringContent(size), "size" },
            { new StringContent("1"), "n" },
        };

        // The parameter that broke every reference render when the deployment moved from
        // gpt-image-1 to gpt-image-2. Asked for only where it is actually supported.
        if (!omit.Contains(ImageModelCapabilities.InputFidelity)
            && capabilities.Supports(deployment, ImageModelCapabilities.InputFidelity))
        {
            form.Add(new StringContent("high"), ImageModelCapabilities.InputFidelity);
        }

        foreach (var reference in references)
        {
            var image = new ByteArrayContent(reference.Bytes);
            image.Headers.ContentType = new MediaTypeHeaderValue(reference.ContentType);
            form.Add(image, "image[]", reference.FileName);
        }
        return form;
    }
}

/// <summary>
/// A non-Foundry image provider declared under <c>Ai:Providers:{name}</c> (ADR-015).
/// The key doubles as the model alias a slot asks for; the credential always lives in the
/// encrypted per-user store, never in config.
/// </summary>
public abstract class ConfiguredImageProvider(
    string name, AiOptions.ImageProviderOptions options, IUserSecretsService secrets) : IImageProvider
{
    protected AiOptions.ImageProviderOptions Options { get; } = options;
    protected IUserSecretsService Secrets { get; } = secrets;

    public string Name { get; } = name;

    public virtual bool IsEnabled => Options.Enabled == true && !string.IsNullOrWhiteSpace(Options.Endpoint);

    /// <summary>Which stored credential this provider uses — one per vendor, so a
    /// workspace can hold a Gemini key and an OpenAI key at the same time.</summary>
    public SecretKind Credential => Options.Credential;

    public abstract bool SupportsReferenceImages { get; }

    public async Task<ImageProviderStatus> StatusAsync(Guid userId, CancellationToken ct)
    {
        // Reference support is reported whatever the readiness: it is a fact about the
        // provider, not about this workspace's key, and the studio needs both to explain
        // itself ("unavailable: no key" vs "unavailable for cards with references").
        if (!IsEnabled)
        {
            return new ImageProviderStatus(
                Name, false, $"Ai:Providers:{Name} is disabled or has no Endpoint.", SupportsReferenceImages);
        }
        var key = await ResolveKeyAsync(userId, ct);
        return string.IsNullOrWhiteSpace(key)
            ? new ImageProviderStatus(Name, false,
                $"No API key stored. Add one in Settings (secret {Credential}).", SupportsReferenceImages)
            : new ImageProviderStatus(Name, true, null, SupportsReferenceImages);
    }

    /// <summary>
    /// The vendor-specific credential, falling back to the legacy shared
    /// <see cref="SecretKind.ImageProviderKey"/> so a workspace that stored a key before
    /// providers had their own slot keeps working.
    /// </summary>
    protected async Task<string?> ResolveKeyAsync(Guid userId, CancellationToken ct) =>
        await Secrets.GetAsync(userId, Credential, ct)
        ?? (Credential == SecretKind.ImageProviderKey
            ? null
            : await Secrets.GetAsync(userId, SecretKind.ImageProviderKey, ct));

    protected async Task<string> RequireKeyAsync(Guid userId, CancellationToken ct)
    {
        if (!IsEnabled)
        {
            throw new AiNotConfiguredException($"Image provider '{Name}' is not enabled.");
        }
        return await ResolveKeyAsync(userId, ct)
            ?? throw new AiNotConfiguredException(
                $"No API key stored for '{Name}'. Add one in Settings → AI keys ({Credential}).");
    }

    /// <summary>The model to ask for: an explicit alias wins, otherwise the configured default.</summary>
    protected string ModelFor(string? modelAlias) =>
        string.IsNullOrWhiteSpace(modelAlias) || modelAlias.Equals(Name, StringComparison.OrdinalIgnoreCase)
            ? Options.Model
            : modelAlias;

    public abstract Task<byte[]> GenerateAsync(
        Guid userId, string prompt, string aspectRatio, string? modelAlias, CancellationToken ct);

    public abstract Task<byte[]> GenerateAsync(
        Guid userId, string prompt, string aspectRatio, string? modelAlias,
        IReadOnlyList<ImageReference> references, CancellationToken ct);

    public virtual Task<(int Width, int Height)> FrameForAsync(
        Guid userId, int targetWidth, int targetHeight, string? modelAlias, CancellationToken ct) =>
        Task.FromResult(ImageAspect.FixedFrame(ImageAspect.Describe(targetWidth, targetHeight)));

    public virtual Task<byte[]> EditAsync(
        Guid userId, string instruction, byte[] image, byte[] maskPng, string? modelAlias, CancellationToken ct) =>
        throw new ImageProviderException($"Image provider '{Name}' does not support region edits.");
}

/// <summary>
/// OpenAI-shaped images API (<c>POST {Endpoint}/images/generations</c>, and
/// <c>/images/edits</c> for reference inputs) — OpenAI's own gpt-image deployments and any
/// vendor that copies that contract.
/// </summary>
public sealed class OpenAiImageProvider(
    string name,
    AiOptions.ImageProviderOptions options,
    IHttpClientFactory httpClients,
    IUserSecretsService secrets,
    IImageModelCapabilities capabilities,
    ILogger logger) : ConfiguredImageProvider(name, options, secrets)
{
    public override bool SupportsReferenceImages => true;

    public override Task<byte[]> GenerateAsync(
        Guid userId, string prompt, string aspectRatio, string? modelAlias, CancellationToken ct) =>
        GenerateAsync(userId, prompt, aspectRatio, modelAlias, [], ct);

    public override async Task<byte[]> GenerateAsync(
        Guid userId, string prompt, string aspectRatio, string? modelAlias,
        IReadOnlyList<ImageReference> references, CancellationToken ct)
    {
        var key = await RequireKeyAsync(userId, ct);
        var model = ModelFor(modelAlias);
        var size = OpenAiShapedImages.SizeFor(aspectRatio);
        var url = new Uri(new Uri(Options.Endpoint.TrimEnd('/') + "/"),
            references.Count == 0 ? "images/generations" : "images/edits");

        return await OpenAiShapedImages.SendWithParameterRepairAsync(
            httpClients.CreateClient("imageprovider"), $"Image provider '{Name}'", model,
            capabilities, logger,
            omit =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Content = references.Count == 0
                    ? OpenAiShapedImages.JsonBody(new { model, prompt, n = 1, size })
                    : BuildEditForm(prompt, size, model, references, omit);
                return request;
            },
            ct);
    }

    public override async Task<byte[]> EditAsync(
        Guid userId, string instruction, byte[] image, byte[] maskPng, string? modelAlias, CancellationToken ct)
    {
        var key = await RequireKeyAsync(userId, ct);
        var model = ModelFor(modelAlias);
        var url = new Uri(new Uri(Options.Endpoint.TrimEnd('/') + "/"), "images/edits");
        return await OpenAiShapedImages.SendWithParameterRepairAsync(
            httpClients.CreateClient("imageprovider"), $"Image provider '{Name}'", model,
            capabilities, logger,
            omit =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Content = OpenAiShapedImages.MaskedEditForm(
                    RegionEdits.EditPrompt(instruction), model, image, maskPng, omit, capabilities);
                return request;
            },
            ct);
    }

    private MultipartFormDataContent BuildEditForm(
        string prompt, string size, string model,
        IReadOnlyList<ImageReference> references, ISet<string> omit)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(prompt), "prompt" },
            { new StringContent(model), "model" },
            { new StringContent(size), "size" },
            { new StringContent("1"), "n" },
        };
        if (!omit.Contains(ImageModelCapabilities.InputFidelity)
            && capabilities.Supports(model, ImageModelCapabilities.InputFidelity))
        {
            form.Add(new StringContent("high"), ImageModelCapabilities.InputFidelity);
        }
        foreach (var reference in references)
        {
            var image = new ByteArrayContent(reference.Bytes);
            image.Headers.ContentType = new MediaTypeHeaderValue(reference.ContentType);
            form.Add(image, "image[]", reference.FileName);
        }
        return form;
    }
}

/// <summary>
/// Google's Gemini image models — "Nano Banana"
/// (<c>POST {Endpoint}/models/{model}:generateContent</c>). A different contract, not a
/// config tweak away from the OpenAI shape: the prompt and any reference images are parts
/// of one content turn, the aspect ratio is a generation-config field rather than a pixel
/// size, and the result comes back as inline base64 on a candidate part.
/// </summary>
public sealed class GeminiImageProvider(
    string name,
    AiOptions.ImageProviderOptions options,
    IHttpClientFactory httpClients,
    IUserSecretsService secrets,
    ILogger logger) : ConfiguredImageProvider(name, options, secrets)
{
    public override bool SupportsReferenceImages => true;

    public override Task<byte[]> GenerateAsync(
        Guid userId, string prompt, string aspectRatio, string? modelAlias, CancellationToken ct) =>
        GenerateAsync(userId, prompt, aspectRatio, modelAlias, [], ct);

    public override async Task<byte[]> GenerateAsync(
        Guid userId, string prompt, string aspectRatio, string? modelAlias,
        IReadOnlyList<ImageReference> references, CancellationToken ct)
    {
        var key = await RequireKeyAsync(userId, ct);
        var model = ModelFor(modelAlias);
        var label = $"Image provider '{Name}' ({model})";

        var parts = new List<object> { new { text = prompt } };
        foreach (var reference in references)
        {
            parts.Add(new
            {
                inline_data = new
                {
                    mime_type = reference.ContentType,
                    data = Convert.ToBase64String(reference.Bytes),
                },
            });
        }

        var body = new
        {
            contents = (object[])[new { role = "user", parts }],
            generationConfig = new
            {
                responseModalities = (string[])["IMAGE"],
                imageConfig = new { aspectRatio = AspectFor(aspectRatio) },
            },
        };

        var url = new Uri(new Uri(Options.Endpoint.TrimEnd('/') + "/"),
            $"models/{Uri.EscapeDataString(model)}:generateContent");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = OpenAiShapedImages.JsonBody(body),
        };
        // Header, never a query string: an API key in a URL reaches request logs.
        request.Headers.TryAddWithoutValidation("x-goog-api-key", key);

        using var response = await httpClients.CreateClient("imageprovider").SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = OpenAiShapedImages.Parse((int)response.StatusCode, payload);
            logger.LogError(
                "{Provider} image request failed: HTTP {Status} code={Code} message={Message}",
                label, error.Status, error.Code ?? "-", error.Message);
            throw OpenAiShapedImages.ToException(label, error);
        }

        return ExtractInlineImage(payload, label);
    }

    public override async Task<byte[]> EditAsync(
        Guid userId, string instruction, byte[] image, byte[] maskPng, string? modelAlias, CancellationToken ct)
    {
        var key = await RequireKeyAsync(userId, ct);
        var model = ModelFor(modelAlias);
        var label = $"Image provider '{Name}' ({model})";
        var body = new
        {
            contents = (object[])[new
            {
                role = "user",
                parts = (object[])
                [
                    new { text = RegionEdits.DescribeRegion(instruction, maskPng)
                        + "\nThe first image is the photo to edit; the second is a mask where white marks the region to change." },
                    new { inline_data = new { mime_type = "image/webp", data = Convert.ToBase64String(image) } },
                    new { inline_data = new { mime_type = "image/png", data = Convert.ToBase64String(maskPng) } },
                ],
            }],
            generationConfig = new { responseModalities = (string[])["IMAGE"] },
        };
        var url = new Uri(new Uri(Options.Endpoint.TrimEnd('/') + "/"), $"models/{Uri.EscapeDataString(model)}:generateContent");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = OpenAiShapedImages.JsonBody(body) };
        request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
        using var response = await httpClients.CreateClient("imageprovider").SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = OpenAiShapedImages.Parse((int)response.StatusCode, payload);
            logger.LogError("{Provider} edit failed: HTTP {Status} code={Code} message={Message}", label, error.Status, error.Code ?? "-", error.Message);
            throw OpenAiShapedImages.ToException(label, error);
        }
        return ExtractInlineImage(payload, label);
    }

    /// <summary>candidates[0].content.parts[*].inlineData.data — the first part that is an image.</summary>
    private static byte[] ExtractInlineImage(string payload, string label)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("candidates", out var candidates)
                && candidates.ValueKind == JsonValueKind.Array)
            {
                foreach (var candidate in candidates.EnumerateArray())
                {
                    if (!candidate.TryGetProperty("content", out var content)
                        || !content.TryGetProperty("parts", out var parts)
                        || parts.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    foreach (var part in parts.EnumerateArray())
                    {
                        var inline = part.TryGetProperty("inlineData", out var camel) ? camel
                            : part.TryGetProperty("inline_data", out var snake) ? snake
                            : default;
                        if (inline.ValueKind == JsonValueKind.Object
                            && inline.TryGetProperty("data", out var data)
                            && data.GetString() is { Length: > 0 } encoded)
                        {
                            return Convert.FromBase64String(encoded);
                        }
                    }
                }
            }

            // No image part: Gemini answers 200 with a text part or a finishReason when it
            // declines, so this is a refusal, not a transport fault — say which.
            var reason = doc.RootElement.TryGetProperty("promptFeedback", out var feedback)
                && feedback.TryGetProperty("blockReason", out var blocked)
                ? blocked.GetString()
                : null;
            throw new ImageProviderException(reason is null
                ? $"{label} returned no image. The prompt may have been declined — try rewording it."
                : $"{label} declined this prompt ({reason}). Try rewording it.");
        }
        catch (JsonException ex)
        {
            throw new ImageProviderException($"{label} returned a response that isn't JSON.", ex);
        }
        catch (FormatException ex)
        {
            throw new ImageProviderException($"{label} returned image data that isn't base64.", ex);
        }
    }

    /// <summary>
    /// Gemini's documented output frames per aspect ratio. It takes a ratio, not a pixel
    /// size, so a 16:9 slot is painted natively at 1344×768 and the crop pass removes
    /// nothing but rounding.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (int Width, int Height)> NativeFrames =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["1:1"] = (1024, 1024),
            ["2:3"] = (832, 1248),
            ["3:2"] = (1248, 832),
            ["3:4"] = (864, 1184),
            ["4:3"] = (1184, 864),
            ["4:5"] = (896, 1152),
            ["5:4"] = (1152, 896),
            ["9:16"] = (768, 1344),
            ["16:9"] = (1344, 768),
            ["21:9"] = (1536, 672),
        };

    /// <summary>The supported ratio closest to the requested one (exact size or ratio string).</summary>
    internal static string AspectFor(string aspectRatio)
    {
        var (w, h) = ImageAspect.Ratio(aspectRatio);
        var wanted = (double)w / h;
        return NativeFrames
            .OrderBy(pair => Math.Abs(Math.Log((double)pair.Value.Width / pair.Value.Height) - Math.Log(wanted)))
            .First().Key;
    }

    public override Task<(int Width, int Height)> FrameForAsync(
        Guid userId, int targetWidth, int targetHeight, string? modelAlias, CancellationToken ct) =>
        Task.FromResult(NativeFrames[AspectFor(ImageAspect.Describe(targetWidth, targetHeight))]);
}

/// <summary>Registers the Foundry provider plus every configured/built-in alternate.</summary>
public static class ImageProviderRegistration
{
    public static IServiceCollection AddImageProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IImageModelCapabilities, ImageModelCapabilities>();
        services.AddScoped<FoundryImageProvider>();

        var configured = configuration
            .GetSection($"{AiOptions.SectionName}:Providers")
            .Get<Dictionary<string, AiOptions.ImageProviderOptions>>() ?? [];

        foreach (var (name, options) in AiOptions.MergeImageProviders(configured))
        {
            services.AddScoped<ConfiguredImageProvider>(sp =>
            {
                var httpClients = sp.GetRequiredService<IHttpClientFactory>();
                var secrets = sp.GetRequiredService<IUserSecretsService>();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger($"Castmill.ImageProvider.{name}");
                return options.Kind.Equals("gemini", StringComparison.OrdinalIgnoreCase)
                    ? new GeminiImageProvider(name, options, httpClients, secrets, logger)
                    : new OpenAiImageProvider(
                        name, options, httpClients, secrets,
                        sp.GetRequiredService<IImageModelCapabilities>(), logger);
            });
        }

        services.AddScoped<IImageProviderRegistry, ImageProviderRegistry>();
        return services;
    }
}
