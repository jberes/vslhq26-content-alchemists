using System.Net;
using Castmill.UI.Http;
using Castmill.UI.Platform;

namespace Castmill.UI.Tests;

/// <summary>
/// Uploads must survive <see cref="CastmillHttpHandler"/>'s silent token refresh, which
/// answers a 401 by REPLAYING the request — and replaying re-reads the body.
///
/// The second test here is deliberate counter-evidence: a one-shot, non-seekable body was the
/// obvious explanation for an upload that failed only in the desktop shell, and it turns out
/// to be wrong, because HttpClient buffers content before sending. Keeping the disproof stops
/// that theory being re-invented.
/// </summary>
public sealed class FileUploadReplayTests
{
    private static readonly byte[] FileBytes = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4];

    [Fact]
    public async Task A_buffered_upload_survives_a_token_refresh_and_replay()
    {
        var tokens = new RefreshOnceProvider();
        var (client, inner) = Build(tokens);

        // 401 first (expired token), 204 on the replay.
        await new ApiClient(client).PostBytesAsync("api/v1/blob/assets/x/content", FileBytes, "image/png");

        Assert.Equal(2, inner.Sends);
        Assert.Equal(1, tokens.RefreshCount);

        // The replay must carry the SAME bytes — a body that replays empty would store a
        // corrupt, zero-length asset, which is worse than failing.
        Assert.Equal(2, inner.BodyLengths.Count);
        Assert.All(inner.BodyLengths, length => Assert.Equal(FileBytes.Length, length));
    }

    /// <summary>
    /// Recorded because it disproved a plausible theory. A one-shot, non-seekable body was the
    /// obvious suspect for the desktop-only upload failure — but HttpClient buffers content
    /// before sending, so the replay reads from that buffer and the upload survives. Streaming
    /// is therefore NOT the fault, and this pins that so the theory is not re-invented.
    ///
    /// Buffering explicitly is still the right call — it makes the guarantee ours rather than
    /// an implementation detail of HttpClient — but it is not the fix for that bug.
    /// </summary>
    [Fact]
    public async Task A_one_shot_stream_body_also_survives_because_HttpClient_buffers_it()
    {
        var tokens = new RefreshOnceProvider();
        var (client, inner) = Build(tokens);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/blob/assets/x/content")
        {
            Content = new StreamContent(new OneShotStream(FileBytes)),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(2, inner.Sends);
        Assert.All(inner.BodyLengths, length => Assert.Equal(FileBytes.Length, length));
    }

    private static (HttpClient Client, CountingHandler Inner) Build(IAuthTokenProvider tokens)
    {
        var inner = new CountingHandler();
        var handler = new CastmillHttpHandler(tokens) { InnerHandler = inner };
        return (new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") }, inner);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }

        public List<int> BodyLengths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            BodyLengths.Add(request.Content is null
                ? 0
                : (await request.Content.ReadAsByteArrayAsync(cancellationToken)).Length);

            return new HttpResponseMessage(
                Sends == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.NoContent);
        }
    }

    /// <summary>A stream that can be read exactly once — what a WebView file handle behaves like.</summary>
    private sealed class OneShotStream(byte[] bytes) : Stream
    {
        private int _position;
        private bool _consumed;

        public override bool CanRead => !_consumed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_consumed)
            {
                throw new ObjectDisposedException(nameof(OneShotStream), "This stream was already read.");
            }

            var take = Math.Min(count, bytes.Length - _position);
            if (take <= 0)
            {
                _consumed = true;
                return 0;
            }

            Array.Copy(bytes, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RefreshOnceProvider : IAuthTokenProvider
    {
        public string? AccessToken { get; private set; } = "stale-token";

        public bool IsSignedIn => AccessToken is not null;

        public int RefreshCount { get; private set; }

        public event Action? Changed;

        public Task<bool> TryRestoreAsync() => Task.FromResult(true);

        public Task StoreAsync(string accessToken, DateTimeOffset accessExpiresAt, string refreshToken)
        {
            AccessToken = accessToken;
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public Task<bool> TryRefreshAsync()
        {
            RefreshCount++;
            AccessToken = "fresh-token";
            return Task.FromResult(true);
        }

        public Task ClearAsync()
        {
            AccessToken = null;
            return Task.CompletedTask;
        }
    }
}
