using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Castmill.UI.Platform;

[UnsupportedOSPlatform("browser")]
public sealed class DesktopLoopbackReceiver : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _path;
    private bool _disposed;

    private DesktopLoopbackReceiver(TcpListener listener, string path)
    {
        _listener = listener;
        _path = path;
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        ReturnUri = new Uri($"http://127.0.0.1:{port}{path}");
    }

    public Uri ReturnUri { get; }

    public static DesktopLoopbackReceiver Start()
    {
        var nonce = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        return new(listener, $"/castmill/auth/{nonce}/");
    }

    public async Task<ExternalAuthCallbackResult?> ReceiveAsync(
        Guid expectedAttemptId,
        DateTimeOffset expiresAt,
        TimeProvider clock,
        CancellationToken ct = default)
    {
        var remaining = expiresAt - clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(remaining);
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(timeout.Token);
            var headerBytes = requestLine?.Length ?? 0;
            string? header;
            do
            {
                header = await reader.ReadLineAsync(timeout.Token);
                headerBytes += header?.Length ?? 0;
                if (headerBytes > 8192)
                {
                    await RespondAsync(stream, HttpStatusCode.RequestHeaderFieldsTooLarge, timeout.Token);
                    return null;
                }
            }
            while (!string.IsNullOrEmpty(header));

            var result = ParseRequestLine(requestLine, expectedAttemptId, _path);
            await RespondAsync(
                stream,
                result is null ? HttpStatusCode.BadRequest : HttpStatusCode.OK,
                timeout.Token);
            return result;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            await DisposeAsync();
        }
    }

    internal static ExternalAuthCallbackResult? ParseRequestLine(
        string? requestLine,
        Guid expectedAttemptId,
        string expectedPath)
    {
        var parts = requestLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not ["GET", var target, "HTTP/1.1"]
            || !Uri.TryCreate("http://127.0.0.1" + target, UriKind.Absolute, out var uri)
            || !string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        var query = QueryHelpers.ParseQuery(uri.Query);
        if (query.Count is < 3 or > 4
            || query.Any(pair => pair.Value.Count != 1)
            || !string.Equals(query["external"], "complete", StringComparison.Ordinal)
            || !Guid.TryParse(query["attemptId"], out var attemptId)
            || attemptId != expectedAttemptId)
        {
            return null;
        }

        var code = query.TryGetValue("code", out var codeValues)
            ? codeValues.ToString()
            : string.Empty;
        var error = query.TryGetValue("error", out var errorValues)
            ? errorValues.ToString()
            : string.Empty;
        if ((code.Length == 0) == (error.Length == 0)
            || (code.Length > 0 && !IsBase64UrlCode(code))
            || error.Length > 100)
        {
            return null;
        }

        return new(attemptId, code.Length == 0 ? null : code, error.Length == 0 ? null : error);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _listener.Stop();
        }
        return ValueTask.CompletedTask;
    }

    private static bool IsBase64UrlCode(string value) =>
        value.Length == 43
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static Task RespondAsync(
        NetworkStream stream,
        HttpStatusCode status,
        CancellationToken ct)
    {
        var body = status == HttpStatusCode.OK
            ? "Authentication complete. Return to Castmill."
            : "Invalid authentication response.";
        var bytes = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: text/plain; charset=utf-8\r\n"
            + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n{body}");
        return stream.WriteAsync(bytes, ct).AsTask();
    }
}