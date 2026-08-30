using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Castmill.Api.Data;
using Castmill.Core.Auth;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Auth;

public interface IExternalAvatarCaptureService
{
    Task CaptureAsync(
        Guid attemptId,
        string provider,
        string accessToken,
        CancellationToken ct);
}

public sealed class ExternalAvatarCaptureService(
    HttpClient http,
    CastmillDbContext db,
    ILogger<ExternalAvatarCaptureService> logger) : IExternalAvatarCaptureService
{
    internal const int MaxAvatarBytes = 256 * 1024;

    public async Task CaptureAsync(
        Guid attemptId,
        string provider,
        string accessToken,
        CancellationToken ct)
    {
        try
        {
            var avatar = provider switch
            {
                ExternalAuthProviders.Microsoft => await FetchMicrosoftAsync(accessToken, ct),
                ExternalAuthProviders.Google => await FetchGoogleAsync(accessToken, ct),
                _ => null,
            };
            if (avatar is null)
            {
                return;
            }

            await db.ExternalAuthAttempts
                .Where(attempt => attempt.Id == attemptId
                    && attempt.Provider == provider
                    && attempt.Status == ExternalAuthStatuses.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(attempt => attempt.CandidateAvatarImage, avatar.Bytes)
                    .SetProperty(attempt => attempt.CandidateAvatarContentType, avatar.ContentType), ct);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or JsonException
            or TaskCanceledException)
        {
            logger.LogWarning(
                "External avatar capture was skipped for {Provider} ({ExceptionType}).",
                provider,
                exception.GetType().Name);
        }
    }

    private async Task<ExternalAvatar?> FetchMicrosoftAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://graph.microsoft.com/v1.0/me/photos/48x48/$value");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await DownloadImageAsync(request, ct);
    }

    private async Task<ExternalAvatar?> FetchGoogleAsync(string accessToken, CancellationToken ct)
    {
        using var userInfoRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "https://openidconnect.googleapis.com/v1/userinfo");
        userInfoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var userInfoResponse = await http.SendAsync(
            userInfoRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (!userInfoResponse.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await userInfoResponse.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("picture", out var picture)
            || picture.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(picture.GetString(), UriKind.Absolute, out var pictureUri)
            || !IsTrustedGoogleImageUri(pictureUri))
        {
            return null;
        }

        using var pictureRequest = new HttpRequestMessage(HttpMethod.Get, pictureUri);
        return await DownloadImageAsync(pictureRequest, ct);
    }

    private async Task<ExternalAvatar?> DownloadImageAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden
            || !response.IsSuccessStatusCode
            || response.Content.Headers.ContentLength > MaxAvatarBytes)
        {
            return null;
        }

        var contentType = CanonicalContentType(response.Content.Headers.ContentType?.MediaType);
        if (contentType is null)
        {
            return null;
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }
            if (destination.Length + read > MaxAvatarBytes)
            {
                return null;
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        var bytes = destination.ToArray();
        return MatchesImageSignature(contentType, bytes)
            ? new ExternalAvatar(bytes, contentType)
            : null;
    }

    internal static bool IsTrustedGoogleImageUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && (string.Equals(uri.Host, "googleusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase));

    internal static string? CanonicalContentType(string? value) => value?.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => "image/jpeg",
        "image/png" => "image/png",
        "image/webp" => "image/webp",
        "image/gif" => "image/gif",
        _ => null,
    };

    internal static bool MatchesImageSignature(string contentType, byte[] bytes) => contentType switch
    {
        "image/jpeg" => bytes is [0xFF, 0xD8, 0xFF, ..],
        "image/png" => bytes is [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..],
        "image/webp" => bytes.Length >= 12
            && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        "image/gif" => bytes.Length >= 6
            && (bytes.AsSpan(0, 6).SequenceEqual("GIF87a"u8)
                || bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8)),
        _ => false,
    };

    private sealed record ExternalAvatar(byte[] Bytes, string ContentType);
}