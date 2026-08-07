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

    private sealed record ValidationProblem(Dictionary<string, string[]>? Errors);
}
