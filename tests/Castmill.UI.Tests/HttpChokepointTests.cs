using System.Net;
using System.Net.Http.Json;
using Castmill.UI.Http;
using Castmill.UI.Platform;

namespace Castmill.UI.Tests;

/// <summary>
/// The chokepoint is the only place auth and error handling exist, so its behaviour is
/// worth testing directly rather than through a screen. Covers F2's gate: "a forced 401 and
/// 412 each render their designed UX" — here at the layer that decides which UX that is.
/// </summary>
public sealed class HttpChokepointTests
{
    [Fact]
    public async Task A_401_triggers_one_silent_refresh_and_replays_the_request()
    {
        var tokens = new RefreshingTokenProvider();
        var inner = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => JsonOk(new { ok = true }));

        var client = Client(tokens, inner);
        var response = await client.GetAsync("api/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, tokens.RefreshCount);
        Assert.Equal(2, inner.Requests.Count);

        // The replay must carry the NEW token, or it would 401 forever.
        Assert.Equal("refreshed-token", inner.Requests[1].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task A_401_from_an_anonymous_call_is_not_refreshed()
    {
        // /auth/login returning 401 means "wrong password". Refreshing would both mask the
        // real answer and burn the stored refresh token.
        var tokens = new RefreshingTokenProvider();
        var inner = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var client = Client(tokens, inner);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login");
        request.Options.Set(CastmillHttpHandler.Anonymous, true);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, tokens.RefreshCount);
        Assert.Single(inner.Requests);
        Assert.Null(inner.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task A_failed_refresh_surfaces_the_401_rather_than_looping()
    {
        var tokens = new RefreshingTokenProvider { CanRefresh = false };
        var inner = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var client = Client(tokens, inner);
        var response = await client.GetAsync("api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task Every_request_carries_a_correlation_id()
    {
        var inner = new SequenceHandler(_ => JsonOk(new { ok = true }));
        var client = Client(new RefreshingTokenProvider(), inner);

        await client.GetAsync("api/v1/me");

        Assert.True(inner.Requests[0].Headers.Contains(CastmillHttpHandler.CorrelationHeader));
    }

    [Fact]
    public async Task A_replayed_request_keeps_its_body_and_its_original_correlation_id()
    {
        var inner = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => JsonOk(new { ok = true }));

        var client = Client(new RefreshingTokenProvider(), inner);
        var api = new ApiClient(client);

        await api.PostAsync("api/v1/campaigns", new { name = "Webinar" });

        Assert.Contains("Webinar", inner.Bodies[1], StringComparison.Ordinal);
        Assert.Equal(inner.Bodies[0], inner.Bodies[1]);
        Assert.Equal(
            inner.Requests[0].Headers.GetValues(CastmillHttpHandler.CorrelationHeader).First(),
            inner.Requests[1].Headers.GetValues(CastmillHttpHandler.CorrelationHeader).First());
    }

    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed, 412)]
    [InlineData(HttpStatusCode.PreconditionRequired, 428)]
    public async Task Conditional_write_failures_become_a_conflict_the_UI_can_explain(
        HttpStatusCode status,
        int expected)
    {
        var inner = new SequenceHandler(_ => new HttpResponseMessage(status));
        var api = new ApiClient(Client(new RefreshingTokenProvider(), inner));

        var ex = await Assert.ThrowsAsync<ConflictApiException>(() =>
            api.PutAsync<object, object>("api/v1/artifacts/1", new { }, etag: "\"v1\""));

        Assert.Equal(expected, ex.StatusCode);
        Assert.NotEmpty(ex.Message);
    }

    [Fact]
    public async Task A_conditional_write_sends_the_etag_as_If_Match()
    {
        var inner = new SequenceHandler(_ => JsonOk(new { ok = true }));
        var api = new ApiClient(Client(new RefreshingTokenProvider(), inner));

        await api.PutAsync<object, object>("api/v1/artifacts/1", new { }, etag: "\"v7\"");

        Assert.Equal("\"v7\"", inner.Requests[0].Headers.GetValues("If-Match").Single());
    }

    [Fact]
    public async Task Validation_problems_keep_their_field_errors()
    {
        var problem = JsonOk(new { errors = new Dictionary<string, string[]> { ["Password"] = ["Too short."] } });
        problem.StatusCode = HttpStatusCode.BadRequest;

        var inner = new SequenceHandler(_ => problem);
        var api = new ApiClient(Client(new RefreshingTokenProvider(), inner));

        var ex = await Assert.ThrowsAsync<ValidationApiException>(() =>
            api.PostAsync<object, object>("api/v1/auth/register", new { }));

        Assert.Contains("Too short.", ex.Errors["Password"]);
    }

    private static HttpClient Client(IAuthTokenProvider tokens, HttpMessageHandler inner) =>
        new(new CastmillHttpHandler(tokens) { InnerHandler = inner })
        {
            BaseAddress = new Uri("https://api.test/"),
        };

    private static HttpResponseMessage JsonOk<T>(T body) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    /// <summary>Returns the queued responses in order; the last one repeats.</summary>
    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        /// <summary>Request bodies as strings, captured while still readable.</summary>
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Read the body here and keep the string: HttpClient disposes request content
            // once the send completes, so it cannot be read from the assertions.
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            Requests.Add(request);
            var index = Math.Min(Requests.Count - 1, responses.Length - 1);
            return responses[index](request);
        }
    }

    private sealed class RefreshingTokenProvider : IAuthTokenProvider
    {
        public string? AccessToken { get; private set; } = "initial-token";

        public bool IsSignedIn => AccessToken is not null;

        public bool CanRefresh { get; init; } = true;

        public int RefreshCount { get; private set; }

        public event Action? Changed;

        public Task<bool> TryRestoreAsync() => Task.FromResult(IsSignedIn);

        public Task StoreAsync(string accessToken, DateTimeOffset accessExpiresAt, string refreshToken)
        {
            AccessToken = accessToken;
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public Task<bool> TryRefreshAsync()
        {
            RefreshCount++;

            if (!CanRefresh)
            {
                return Task.FromResult(false);
            }

            AccessToken = "refreshed-token";
            return Task.FromResult(true);
        }

        public Task ClearAsync()
        {
            AccessToken = null;
            return Task.CompletedTask;
        }
    }
}
