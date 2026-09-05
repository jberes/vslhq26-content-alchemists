using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace Castmill.Desktop.Platform;

/// <summary>
/// Loopback file server for the Mill Floor player (ADR-057). Serves ONLY files registered
/// through <see cref="Register"/>, each behind a random token, with HTTP Range support so
/// the WebView's media element can seek. Binds 127.0.0.1 on an ephemeral port; started on
/// first use, stopped with the process.
/// </summary>
internal sealed class LocalMediaServer : IDisposable
{
    private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private HttpListener? _listener;
    private string? _prefix;

    public string Register(string path)
    {
        EnsureStarted();
        var token = _files.FirstOrDefault(pair => pair.Value == path).Key;
        if (token is null)
        {
            token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            _files[token] = path;
        }
        return $"{_prefix}media/{token}{Path.GetExtension(path)}";
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_listener is not null)
            {
                return;
            }
            // Try a few ephemeral ports: HttpListener cannot bind ":0".
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var port = Random.Shared.Next(41000, 49000);
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    _prefix = $"http://127.0.0.1:{port}/";
                    _ = Task.Run(AcceptLoopAsync);
                    return;
                }
                catch (HttpListenerException)
                {
                    listener.Close();
                }
            }
            throw new InvalidOperationException("No loopback port was available for the media player.");
        }
    }

    private async Task AcceptLoopAsync()
    {
        var listener = _listener!;
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(context));
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var segments = context.Request.Url?.AbsolutePath.Trim('/').Split('/') ?? [];
            if (segments.Length != 2 || segments[0] != "media")
            {
                response.StatusCode = 404;
                return;
            }
            var token = Path.GetFileNameWithoutExtension(segments[1]);
            if (!_files.TryGetValue(token, out var path) || !File.Exists(path))
            {
                response.StatusCode = 404;
                return;
            }

            var info = new FileInfo(path);
            var length = info.Length;
            long start = 0, end = length - 1;
            var range = context.Request.Headers["Range"];
            if (range is not null && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var parts = range[6..].Split('-');
                if (parts.Length == 2)
                {
                    if (long.TryParse(parts[0], out var s)) start = s;
                    if (long.TryParse(parts[1], out var e)) end = Math.Min(e, length - 1);
                }
                if (start > end || start >= length)
                {
                    response.StatusCode = 416;
                    response.Headers["Content-Range"] = $"bytes */{length}";
                    return;
                }
                response.StatusCode = 206;
                response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
            }
            response.Headers["Accept-Ranges"] = "bytes";
            response.Headers["Cache-Control"] = "no-store";
            response.ContentType = ContentTypeFor(path);
            response.ContentLength64 = end - start + 1;

            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            file.Seek(start, SeekOrigin.Begin);
            var remaining = end - start + 1;
            var buffer = new byte[1 << 16];
            while (remaining > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                if (read <= 0)
                {
                    break;
                }
                await response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
                remaining -= read;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException)
        {
            // The player seeked away mid-transfer; nothing to do.
        }
        finally
        {
            try { response.Close(); } catch (ObjectDisposedException) { }
        }
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".aac" => "audio/aac",
        ".ogg" => "audio/ogg",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        _listener?.Close();
    }
}

/// <summary>
/// Size + SHA-256 of the first and last 4 MB: a fingerprint that survives a move or rename,
/// costs milliseconds on a multi-gigabyte recording, and is what "Locate file…" checks
/// before re-linking (ADR-057).
/// </summary>
internal static class MediaFingerprint
{
    private const int Window = 4 * 1024 * 1024;

    public static string Compute(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = stream.Length;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[Window];
        var head = stream.Read(buffer, 0, (int)Math.Min(Window, length));
        sha.AppendData(buffer, 0, head);
        if (length > Window)
        {
            stream.Seek(Math.Max(0, length - Window), SeekOrigin.Begin);
            var tail = stream.Read(buffer, 0, Window);
            sha.AppendData(buffer, 0, tail);
        }
        return $"sz{length}-{Convert.ToHexStringLower(sha.GetHashAndReset())[..32]}";
    }
}
