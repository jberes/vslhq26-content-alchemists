using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Castmill.UI.Http;

/// <summary>A file the API returned, ready to be handed to the browser's download path.</summary>
public sealed record DownloadedFile(string FileName, string ContentType, byte[] Bytes);

/// <summary>
/// Thin typed wrapper over the chokepoint's HttpClient. Its job is response interpretation:
/// turning status codes into the typed envelope and capturing ETags for conditional writes.
/// Feature-specific clients (campaigns, artifacts) build on this in later phases.
/// </summary>
public sealed class ApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<T> GetAsync<T>(string url, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(url, ct);
        return await ReadAsync<T>(response, ct);
    }

    /// <summary>
    /// GET returning raw bytes and the server's own file name. For downloads: the export
    /// endpoints are authenticated, so a plain link cannot fetch them — the Bearer token
    /// lives in the client, not in a cookie.
    /// </summary>
    public async Task<DownloadedFile> DownloadAsync(string url, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(url, ct);
        await ThrowIfFailedAsync(response, ct);

        var name = response.Content.Headers.ContentDisposition?.FileNameStar
                   ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                   ?? "download";

        return new DownloadedFile(
            name,
            response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
            await response.Content.ReadAsByteArrayAsync(ct));
    }

    /// <summary>GET that also surfaces the ETag, for anything that will later be written back.</summary>
    public async Task<(T Value, string? ETag)> GetWithETagAsync<T>(string url, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(url, ct);
        var value = await ReadAsync<T>(response, ct);
        return (value, response.Headers.ETag?.ToString());
    }

    public async Task<TResponse> PostAsync<TRequest, TResponse>(
        string url,
        TRequest body,
        bool anonymous = false,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: Json),
        };

        if (anonymous)
        {
            request.Options.Set(CastmillHttpHandler.Anonymous, true);
        }

        using var response = await http.SendAsync(request, ct);
        return await ReadAsync<TResponse>(response, ct);
    }

    public async Task PostAsync<TRequest>(
        string url,
        TRequest body,
        bool anonymous = false,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: Json),
        };

        if (anonymous)
        {
            request.Options.Set(CastmillHttpHandler.Anonymous, true);
        }

        using var response = await http.SendAsync(request, ct);
        await ThrowIfFailedAsync(response, ct);
    }

    /// <summary>
    /// Conditional write. Omitting the ETag is what produces a 428 from the server, so the
    /// parameter is required rather than optional — forgetting it should be a compile-time
    /// decision, not a runtime surprise.
    /// </summary>
    public async Task<TResponse> PutAsync<TRequest, TResponse>(
        string url,
        TRequest body,
        string? etag,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(body, options: Json),
        };

        if (!string.IsNullOrEmpty(etag))
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        using var response = await http.SendAsync(request, ct);
        return await ReadAsync<TResponse>(response, ct);
    }

    /// <summary>
    /// PUT/PATCH whose success is a 204. The generic overloads throw on an empty body — which
    /// is correct for reads and silently wrong for writes whose whole answer IS "no content":
    /// saving a secret, saving the workspace links and renaming a brand asset all failed with
    /// "The server returned an empty response." while the server had done the work.
    /// </summary>
    public async Task PutAsync<TRequest>(string url, TRequest body, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync(url, body, Json, ct);
        await ThrowIfFailedAsync(response, ct);
    }

    public async Task PatchAsync<TRequest>(string url, TRequest body, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        using var response = await http.SendAsync(request, ct);
        await ThrowIfFailedAsync(response, ct);
    }

    public async Task DeleteAsync(string url, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync(url, ct);
        await ThrowIfFailedAsync(response, ct);
    }

    /// <summary>Conditional POST — restore-style actions that are writes in POST clothing.</summary>
    public async Task<TResponse> PostWithETagAsync<TResponse>(
        string url,
        string? etag,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { }, options: Json),
        };

        if (!string.IsNullOrEmpty(etag))
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        using var response = await http.SendAsync(request, ct);
        return await ReadAsync<TResponse>(response, ct);
    }

    /// <summary>
    /// Conditional PATCH. Same ETag discipline as <see cref="PutAsync{TRequest,TResponse}"/>:
    /// the server refuses a status change without an If-Match (428).
    /// </summary>
    public async Task<TResponse> PatchAsync<TRequest, TResponse>(
        string url,
        TRequest body,
        string? etag,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(body, options: Json),
        };

        if (!string.IsNullOrEmpty(etag))
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        using var response = await http.SendAsync(request, ct);
        return await ReadAsync<TResponse>(response, ct);
    }

    /// <summary>
    /// POSTs file bytes. Goes through the same handler as everything else, so the bearer
    /// token, correlation ID and typed errors are unchanged.
    ///
    /// The body is BUFFERED into a byte array rather than streamed, for two reasons that both
    /// bite only outside the web shell:
    ///
    ///  1. A file stream from the WebView is not seekable and has no known length, so it is
    ///     sent chunked and can be read exactly once. CastmillHttpHandler replays a request
    ///     after a silent token refresh, and replaying re-reads the content — on a consumed
    ///     one-shot stream that throws, which surfaces as "couldn't reach the API" even though
    ///     the API answered perfectly.
    ///  2. Blazor Hybrid reads an IBrowserFile over JS interop in chunks, so a streamed body
    ///     is at the mercy of interop read timeouts mid-upload.
    ///
    /// Kit images are capped at 20 MB, so holding one in memory costs nothing next to being
    /// able to retry it.
    /// </summary>
    public async Task PostBytesAsync(
        string url, byte[] content, string contentType, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(content),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        using var response = await http.SendAsync(request, ct);
        await ThrowIfFailedAsync(response, ct);
    }

    public async Task<TResponse> PutBytesAsync<TResponse>(
        string url,
        byte[] content,
        string contentType,
        string sha256,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new ByteArrayContent(content),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        request.Headers.TryAddWithoutValidation("X-Content-SHA256", sha256);

        using var response = await http.SendAsync(request, ct);
        return await ReadAsync<TResponse>(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await ThrowIfFailedAsync(response, ct);

        var value = await response.Content.ReadFromJsonAsync<T>(Json, ct);
        return value ?? throw new ApiException(
            "The server returned an empty response.", (int)response.StatusCode, Correlation(response));
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var correlationId = Correlation(response);

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new UnauthorizedApiException(correlationId),

            HttpStatusCode.PreconditionFailed => new ConflictApiException(
                "Someone else changed this while you were editing.", 412, correlationId),

            HttpStatusCode.PreconditionRequired => new ConflictApiException(
                "This change was sent without a version. Reload and try again.", 428, correlationId),

            HttpStatusCode.BadRequest => await ValidationOrGenericAsync(response, correlationId, ct),

            HttpStatusCode.TooManyRequests => new ApiException(
                "That was a lot at once — give it a moment and try again.", 429, correlationId),

            _ => new ApiException(
                await ProblemDetailAsync(response, ct)
                    ?? $"The server returned {(int)response.StatusCode}.",
                (int)response.StatusCode, correlationId),
        };
    }

    /// <summary>
    /// The server explains its 5xx/409s through ProblemDetails ("DataForSEO is not
    /// configured…", "No Foundry deployment for alias…"). Surfacing that sentence is the
    /// difference between a fixable error and a dead "the server returned 503".
    /// </summary>
    private static async Task<string?> ProblemDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Json, ct);
            return string.IsNullOrWhiteSpace(problem?.Detail)
                ? (string.IsNullOrWhiteSpace(problem?.Title) ? null : problem!.Title)
                : problem!.Detail;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed record ProblemBody(string? Title, string? Detail);

    private static async Task<ApiException> ValidationOrGenericAsync(
        HttpResponseMessage response,
        string? correlationId,
        CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblem>(Json, ct);
            if (problem?.Errors is { Count: > 0 })
            {
                return new ValidationApiException(problem.Errors, correlationId);
            }
            var message = string.IsNullOrWhiteSpace(problem?.Detail)
                ? problem?.Title
                : problem.Detail;
            if (!string.IsNullOrWhiteSpace(message))
            {
                return new ApiException(message, 400, correlationId);
            }
        }
        catch (JsonException)
        {
            // Not a problem-details body; fall through to the generic message.
        }

        return new ApiException("That request wasn't valid.", 400, correlationId);
    }

    private static string? Correlation(HttpResponseMessage response) =>
        response.Headers.TryGetValues(CastmillHttpHandler.CorrelationHeader, out var values)
            ? values.FirstOrDefault()
            : null;

    private sealed record ValidationProblem(
        Dictionary<string, string[]>? Errors,
        string? Title,
        string? Detail);
}
