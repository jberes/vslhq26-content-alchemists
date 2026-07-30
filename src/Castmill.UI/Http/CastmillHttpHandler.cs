using System.Net;
using System.Net.Http.Headers;
using Castmill.UI.Platform;

namespace Castmill.UI.Http;

/// <summary>
/// The single HTTP chokepoint (Frontend-Architecture.md §4). Every API call passes through
/// here, so there is exactly one place that:
///   • attaches the bearer token,
///   • generates and propagates a correlation ID,
///   • performs one silent refresh on a 401 and replays the request,
///   • converts failures into the typed envelope in ApiErrors.cs.
///
/// UI code never touches HttpClient directly, which is what keeps auth and error handling
/// from being reimplemented per feature.
/// </summary>
public sealed class CastmillHttpHandler(IAuthTokenProvider tokens) : DelegatingHandler
{
    public const string CorrelationHeader = "X-Correlation-ID";

    /// <summary>Set on a request to opt out of the bearer header (the auth endpoints themselves).</summary>
    public static readonly HttpRequestOptionsKey<bool> Anonymous = new("castmill.anonymous");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var correlationId = Guid.NewGuid().ToString("n");
        request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);

        var anonymous = request.Options.TryGetValue(Anonymous, out var flag) && flag;
        if (!anonymous)
        {
            Authorize(request, tokens.AccessToken);
        }

        var response = await base.SendAsync(request, cancellationToken);

        // One silent refresh, then replay. Anonymous calls are excluded: a 401 from
        // /auth/login means "wrong password", and refreshing would mask it.
        if (response.StatusCode == HttpStatusCode.Unauthorized && !anonymous)
        {
            if (await tokens.TryRefreshAsync())
            {
                response.Dispose();

                var replay = await CloneAsync(request, cancellationToken);
                replay.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);
                Authorize(replay, tokens.AccessToken);

                response = await base.SendAsync(replay, cancellationToken);
            }
        }

        return response;
    }

    private static void Authorize(HttpRequestMessage request, string? accessToken)
    {
        request.Headers.Authorization = string.IsNullOrEmpty(accessToken)
            ? null
            : new AuthenticationHeaderValue("Bearer", accessToken);
    }

    /// <summary>
    /// A sent HttpRequestMessage cannot be reused, so a replay needs a copy. The body is
    /// buffered because the original content stream is already consumed.
    /// </summary>
    private static async Task<HttpRequestMessage> CloneAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
        };

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var content = new ByteArrayContent(bytes);

            foreach (var header in request.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        foreach (var header in request.Headers)
        {
            if (!string.Equals(header.Key, CorrelationHeader, StringComparison.OrdinalIgnoreCase))
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        }

        return clone;
    }
}
